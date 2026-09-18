using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Merconiq.Web.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Merconiq.Tests.Infrastructure;

public class WebhookDispatcherTests
{
    [Fact]
    public async Task EnqueueAsync_accepts_an_envelope_at_the_exact_256_KiB_limit()
    {
        const string tenantId = "tenant-payload-limit";
        var databaseName = Guid.NewGuid().ToString("N");
        await using var context = CreateContext(databaseName, tenantId);
        context.WebhookSubscriptions.AddRange(
            NewSubscription(tenantId, "https://hooks.example.test/first"),
            NewSubscription(tenantId, "https://hooks.example.test/second"));
        await context.SaveChangesAsync();

        var webhookEvent = CreateEventAtSerializedSize(WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes);
        var expectedPayload = JsonSerializer.Serialize(webhookEvent);
        Encoding.UTF8.GetByteCount(expectedPayload).Should()
            .Be(WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes);

        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new WebhookDispatcher(
            serviceProvider,
            Mock.Of<IHttpClientFactory>(),
            NullLogger<WebhookDispatcher>.Instance,
            context);

        await dispatcher.EnqueueAsync(webhookEvent);
        await context.SaveChangesAsync();

        var deliveries = await context.WebhookDeliveries.AsNoTracking().ToListAsync();
        deliveries.Should().HaveCount(2);
        deliveries.Should().OnlyContain(delivery => delivery.Payload == expectedPayload);
    }

    [Fact]
    public async Task EnqueueAsync_rejects_an_oversized_envelope_before_adding_any_delivery_rows()
    {
        const string tenantId = "tenant-payload-limit";
        var databaseName = Guid.NewGuid().ToString("N");
        await using var context = CreateContext(databaseName, tenantId);
        context.WebhookSubscriptions.AddRange(
            NewSubscription(tenantId, "https://hooks.example.test/first"),
            NewSubscription(tenantId, "https://hooks.example.test/second"));
        await context.SaveChangesAsync();

        var webhookEvent = CreateEventAtSerializedSize(WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes + 1);
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new WebhookDispatcher(
            serviceProvider,
            Mock.Of<IHttpClientFactory>(),
            NullLogger<WebhookDispatcher>.Instance,
            context);
        Func<Task> enqueue = () => dispatcher.EnqueueAsync(webhookEvent);

        var exception = await enqueue.Should().ThrowAsync<WebhookPayloadTooLargeException>();
        exception.Which.Message.Should().Be(WebhookPayloadPolicy.OversizedEnvelopeDiagnostic);
        context.ChangeTracker.Entries<WebhookDelivery>().Should().BeEmpty();
        (await context.WebhookDeliveries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EnqueueAsync_does_not_serialize_an_event_when_no_subscription_matches()
    {
        const string tenantId = "tenant-payload-limit";
        var databaseName = Guid.NewGuid().ToString("N");
        await using var context = CreateContext(databaseName, tenantId);
        var unrelatedSubscription = NewSubscription(tenantId, "https://hooks.example.test/unrelated");
        unrelatedSubscription.EventType = "Stock.Low";
        context.WebhookSubscriptions.Add(unrelatedSubscription);
        await context.SaveChangesAsync();

        var webhookEvent = CreateEventAtSerializedSize(WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes + 1);
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new WebhookDispatcher(
            serviceProvider,
            Mock.Of<IHttpClientFactory>(),
            NullLogger<WebhookDispatcher>.Instance,
            context);

        await dispatcher.EnqueueAsync(webhookEvent);
        await context.SaveChangesAsync();

        (await context.WebhookDeliveries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_does_not_serialize_an_event_when_no_subscription_matches()
    {
        var repository = new Mock<IRepository<WebhookSubscription>>();
        repository.Setup(repo => repo.FindAsync(It.IsAny<Expression<Func<WebhookSubscription, bool>>>()))
            .ReturnsAsync(Array.Empty<WebhookSubscription>());

        var services = new ServiceCollection();
        services.AddScoped<IRepository<WebhookSubscription>>(_ => repository.Object);
        services.AddScoped<ITenantContext, TenantContext>();
        using var serviceProvider = services.BuildServiceProvider();
        var handler = new CountingHandler();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(factory => factory.CreateClient("Webhooks"))
            .Returns(new HttpClient(handler));
        var dispatcher = new WebhookDispatcher(
            serviceProvider,
            httpClientFactory.Object,
            NullLogger<WebhookDispatcher>.Instance);
        var webhookEvent = CreateEventAtSerializedSize(WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes + 1);

        await dispatcher.DispatchAsync(webhookEvent);

        handler.SendCount.Should().Be(0);
        repository.Verify(repo => repo.FindAsync(It.IsAny<Expression<Func<WebhookSubscription, bool>>>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_rejects_an_oversized_envelope_after_finding_a_matching_subscription()
    {
        var repository = new Mock<IRepository<WebhookSubscription>>();
        repository.Setup(repo => repo.FindAsync(It.IsAny<Expression<Func<WebhookSubscription, bool>>>()))
            .ReturnsAsync([new WebhookSubscription { Url = "https://hooks.example.test/inventory", EventType = "Stock.Received" }]);

        var services = new ServiceCollection();
        services.AddScoped<IRepository<WebhookSubscription>>(_ => repository.Object);
        services.AddScoped<ITenantContext, TenantContext>();
        using var serviceProvider = services.BuildServiceProvider();
        var handler = new CountingHandler();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(factory => factory.CreateClient("Webhooks"))
            .Returns(new HttpClient(handler));
        var dispatcher = new WebhookDispatcher(
            serviceProvider,
            httpClientFactory.Object,
            NullLogger<WebhookDispatcher>.Instance);
        var webhookEvent = CreateEventAtSerializedSize(WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes + 1);
        Func<Task> dispatch = () => dispatcher.DispatchAsync(webhookEvent);

        var exception = await dispatch.Should().ThrowAsync<WebhookPayloadTooLargeException>();

        exception.Which.Message.Should().Be(WebhookPayloadPolicy.OversizedEnvelopeDiagnostic);
        repository.Verify(repo => repo.FindAsync(It.IsAny<Expression<Func<WebhookSubscription, bool>>>()), Times.Once);
        handler.SendCount.Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_SendsSignedEventToSubscribedEndpoint()
    {
        var subscription = new WebhookSubscription
        {
            Url = "https://hooks.example.test/inventory",
            EventType = "Stock.Low",
            Secret = "shared-secret"
        };
        var repository = new Mock<IRepository<WebhookSubscription>>();
        repository.Setup(repo => repo.FindAsync(It.IsAny<Expression<Func<WebhookSubscription, bool>>>()))
            .ReturnsAsync([subscription]);

        var services = new ServiceCollection();
        services.AddScoped<IRepository<WebhookSubscription>>(_ => repository.Object);
        services.AddScoped<ITenantContext, TenantContext>();
        using var serviceProvider = services.BuildServiceProvider();

        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(factory => factory.CreateClient("Webhooks")).Returns(httpClient);
        var dispatcher = new WebhookDispatcher(
            serviceProvider,
            httpClientFactory.Object,
            NullLogger<WebhookDispatcher>.Instance);

        var webhookEvent = WebhookEventFactory.Create(
            new TestTenantContext("tenant-a"),
            "Stock.Low",
            new { ItemId = 42, TotalStock = 3 });
        await dispatcher.DispatchAsync(webhookEvent);

        handler.EventHeader.Should().Be("Stock.Low");
        handler.EventIdHeader.Should().Be(webhookEvent.EventId.ToString("D"));
        handler.DeliveryIdHeader.Should().BeNull();
        handler.Body.Should().Contain($"\"TenantId\":\"{webhookEvent.TenantId}\"");
        handler.Body.Should().Contain($"\"EventId\":\"{webhookEvent.EventId}\"");
        handler.Body.Should().Contain("\"EventType\":\"Stock.Low\"");
        handler.Body.Should().Contain("\"ItemId\":42");
        var expectedSignature = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("shared-secret"), Encoding.UTF8.GetBytes(handler.Body)))
            .ToLowerInvariant();
        handler.Signature.Should().Be(expectedSignature);
    }

    private static InventoryDbContext CreateContext(string databaseName, string tenantId) =>
        new(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options,
            new TestTenantContext(tenantId));

    private static WebhookSubscription NewSubscription(string tenantId, string url) => new()
    {
        TenantId = tenantId,
        Url = url,
        EventType = "Stock.Received"
    };

    private static WebhookEvent<string> CreateEventAtSerializedSize(int serializedSize)
    {
        var webhookEvent = new WebhookEvent<string>(
            Guid.Empty,
            "tenant-payload-limit",
            "Stock.Received",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            string.Empty);
        var emptyEnvelopeSize = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(webhookEvent));
        var payloadLength = serializedSize - emptyEnvelopeSize;
        payloadLength.Should().BePositive();
        return webhookEvent with { Payload = new string('x', payloadLength) };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;
        public string? EventHeader { get; private set; }
        public string? EventIdHeader { get; private set; }
        public string? DeliveryIdHeader { get; private set; }
        public string? Signature { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            EventHeader = request.Headers.GetValues("X-Inventory-Event").Single();
            EventIdHeader = request.Headers.GetValues("X-Inventory-Event-Id").Single();
            DeliveryIdHeader = request.Headers.TryGetValues("X-Inventory-Delivery-Id", out var deliveryIds)
                ? deliveryIds.Single()
                : null;
            Signature = request.Headers.GetValues("X-Inventory-Signature").Single();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

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
