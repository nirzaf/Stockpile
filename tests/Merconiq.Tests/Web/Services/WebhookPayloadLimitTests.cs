using System.Net;
using System.Text;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Tests.Infrastructure;
using Merconiq.Web.BackgroundServices;
using Merconiq.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Merconiq.Tests.Web.Services;

public sealed class WebhookPayloadLimitTests
{
    [Fact]
    public async Task Worker_dead_letters_an_oversized_persisted_payload_without_sending_it()
    {
        var tenantId = $"webhook-payload-limit-{Guid.NewGuid():N}";
        var databaseName = Guid.NewGuid().ToString("N");
        var eventId = Guid.NewGuid();
        const string subscriptionUrl = "https://8.8.8.8/webhook";
        const string payloadMarker = "legacy-payload-private-marker";
        var oversizedPayload = $"{{\"payload\":\"{new string('é', WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes / 2)}{payloadMarker}\"}}";
        Encoding.UTF8.GetByteCount(oversizedPayload)
            .Should().BeGreaterThan(WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes);
        oversizedPayload.Length.Should().BeLessThan(WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes);

        var handler = new CountingHandler();
        var services = new ServiceCollection();
        services.AddScoped<TenantContext>();
        services.AddScoped<InventoryDbContext>(_ => CreateContext(databaseName, tenantId));
        services.AddHttpClient("Webhooks")
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        await using var provider = services.BuildServiceProvider();
        long deliveryId;
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
                EventId = eventId,
                SubscriptionId = subscription.Id,
                EventType = "Stock.Received",
                Payload = oversizedPayload,
                NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            setup.WebhookDeliveries.Add(delivery);
            await setup.SaveChangesAsync();
            deliveryId = delivery.Id;
        }

        var worker = new WebhookDeliveryBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<WebhookDeliveryBackgroundService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var delivery = await WaitForDeadLetterAsync(databaseName, tenantId, deliveryId);

            handler.SendCount.Should().Be(0);
            delivery.LastError.Should().Be(WebhookPayloadPolicy.OversizedEnvelopeDiagnostic);
            delivery.LastError.Should().NotContain(payloadMarker);
            Encoding.UTF8.GetByteCount(delivery.LastError!)
                .Should().BeLessThan(4096);
            delivery.LastStatusCode.Should().BeNull();
            delivery.LastResponse.Should().BeNull();
            delivery.LeaseToken.Should().BeNull();
            delivery.LeaseUntil.Should().BeNull();
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(stopTimeout.Token);
        }
    }

    private static async Task<WebhookDelivery> WaitForDeadLetterAsync(
        string databaseName,
        string tenantId,
        long deliveryId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var verify = CreateContext(databaseName, tenantId);
            var delivery = await verify.WebhookDeliveries.SingleAsync(item => item.Id == deliveryId);
            if (delivery.Status == WebhookDeliveryStatus.DeadLetter)
            {
                return delivery;
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

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
