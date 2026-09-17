using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Merconiq.Tests.Infrastructure;
using Merconiq.Web.BackgroundServices;
using Merconiq.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Merconiq.Tests.Web.Services;

public sealed class WebhookIdentityHeaderTests
{
    [Fact]
    public async Task Durable_worker_sends_persisted_event_identity_instead_of_delivery_row_id()
    {
        var tenantId = $"webhook-identity-{Guid.NewGuid():N}";
        var databaseName = Guid.NewGuid().ToString("N");
        var persistedEventId = Guid.NewGuid();
        const string subscriptionUrl = "https://8.8.8.8/webhook";
        var handler = new RecordingHandler();
        var services = new ServiceCollection();
        services.AddScoped<TenantContext>();
        services.AddScoped<InventoryDbContext>(_ => CreateContext(databaseName, tenantId));
        services.AddHttpClient("Webhooks")
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        await using var provider = services.BuildServiceProvider();
        long deliveryRowId;
        await using (var setup = CreateContext(databaseName, tenantId))
        {
            var subscription = new WebhookSubscription
            {
                TenantId = tenantId,
                Url = subscriptionUrl,
                EventType = "Stock.Received"
            };
            setup.WebhookSubscriptions.Add(subscription);
            await setup.SaveChangesAsync();

            var delivery = new WebhookDelivery
            {
                TenantId = tenantId,
                EventId = persistedEventId,
                SubscriptionId = subscription.Id,
                EventType = "Stock.Received",
                Payload = JsonSerializer.Serialize(new { EventId = persistedEventId, TenantId = tenantId }),
                NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            setup.WebhookDeliveries.Add(delivery);
            await setup.SaveChangesAsync();
            deliveryRowId = delivery.Id;
        }

        var worker = new WebhookDeliveryBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<WebhookDeliveryBackgroundService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var outbound = await handler.RequestCaptured.Task.WaitAsync(TimeSpan.FromSeconds(10));

            outbound.EventIdHeader.Should().Be(persistedEventId.ToString("D"));
            outbound.EventIdHeader.Should().NotBe(deliveryRowId.ToString(CultureInfo.InvariantCulture));
            outbound.DeliveryIdHeader.Should().BeNull();
            using var body = JsonDocument.Parse(outbound.Body);
            body.RootElement.GetProperty("EventId").GetGuid().Should().Be(persistedEventId);

            await WaitForDeliveryCompletionAsync(databaseName, tenantId, deliveryRowId);
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(stopTimeout.Token);
        }
    }

    private static async Task WaitForDeliveryCompletionAsync(
        string databaseName,
        string tenantId,
        long deliveryRowId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var verify = CreateContext(databaseName, tenantId);
            var delivery = await verify.WebhookDeliveries.SingleAsync(item => item.Id == deliveryRowId);
            if (delivery.Status == WebhookDeliveryStatus.Delivered)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private static InventoryDbContext CreateContext(string databaseName, string tenantId) =>
        new(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options,
            new TestTenantContext(tenantId));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<OutboundWebhook> RequestCaptured { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var eventIdHeader = request.Headers.GetValues("X-Inventory-Event-Id").Single();
            var deliveryIdHeader = request.Headers.TryGetValues("X-Inventory-Delivery-Id", out var deliveryIds)
                ? deliveryIds.Single()
                : null;
            RequestCaptured.TrySetResult(new OutboundWebhook(body, eventIdHeader, deliveryIdHeader));

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed record OutboundWebhook(string Body, string EventIdHeader, string? DeliveryIdHeader);
}
