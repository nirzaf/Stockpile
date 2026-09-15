using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace InventoryManagementSystem.Tests.Infrastructure;

public class WebhookDispatcherTests
{
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
        using var serviceProvider = services.BuildServiceProvider();

        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(factory => factory.CreateClient("Webhooks")).Returns(httpClient);
        var dispatcher = new WebhookDispatcher(
            serviceProvider,
            httpClientFactory.Object,
            NullLogger<WebhookDispatcher>.Instance);

        await dispatcher.DispatchAsync("Stock.Low", new { ItemId = 42, TotalStock = 3 });

        handler.EventHeader.Should().Be("Stock.Low");
        handler.Body.Should().Contain("\"ItemId\":42");
        var expectedSignature = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes("shared-secret"), Encoding.UTF8.GetBytes(handler.Body)))
            .ToLowerInvariant();
        handler.Signature.Should().Be(expectedSignature);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string Body { get; private set; } = string.Empty;
        public string? EventHeader { get; private set; }
        public string? Signature { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            EventHeader = request.Headers.GetValues("X-Inventory-Event").Single();
            Signature = request.Headers.GetValues("X-Inventory-Signature").Single();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
