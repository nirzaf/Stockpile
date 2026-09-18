using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace Merconiq.Infrastructure.Services;

public class WebhookDispatcher : IWebhookDispatcher
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookDispatcher> _logger;
    private readonly InventoryDbContext? _context;

    public WebhookDispatcher(
        IServiceProvider serviceProvider,
        IHttpClientFactory httpClientFactory,
        ILogger<WebhookDispatcher> logger,
        InventoryDbContext? context = null)
    {
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _context = context;
    }

    public async Task EnqueueAsync<T>(
        WebhookEvent<T> webhookEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_context is null)
        {
            throw new InvalidOperationException("Webhook enqueueing requires a scoped database context.");
        }

        if (!string.Equals(_context.CurrentTenantId, webhookEvent.TenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Webhook event tenant does not match the current database context.");
        }

        var subscriptions = await _context.WebhookSubscriptions
            .AsNoTracking()
            .Where(subscription => subscription.IsActive &&
                (subscription.EventType == webhookEvent.EventType || subscription.EventType == "*"))
            .ToListAsync(cancellationToken);

        if (subscriptions.Count == 0)
        {
            return;
        }

        var payload = WebhookPayloadPolicy.Serialize(webhookEvent);
        var now = DateTimeOffset.UtcNow;
        foreach (var subscription in subscriptions)
        {
            _context.WebhookDeliveries.Add(new WebhookDelivery
            {
                EventId = webhookEvent.EventId,
                TenantId = webhookEvent.TenantId,
                SubscriptionId = subscription.Id,
                EventType = webhookEvent.EventType,
                Payload = payload,
                NextAttemptAt = now,
                CreatedAt = now
            });
        }
    }

    public async Task DispatchAsync<T>(WebhookEvent<T> webhookEvent)
    {
        List<WebhookSubscription> subscriptions;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            tenantContext.SetTenant(webhookEvent.TenantId);
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<WebhookSubscription>>();
            subscriptions = (await repo.FindAsync(s => s.IsActive &&
                (s.EventType == webhookEvent.EventType || s.EventType == "*"))).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch webhook event {EventId} for tenant {TenantId}",
                webhookEvent.EventId, webhookEvent.TenantId);
            return;
        }

        if (subscriptions.Count == 0)
        {
            return;
        }

        var jsonPayload = WebhookPayloadPolicy.Serialize(webhookEvent);
        try
        {
            var client = _httpClientFactory.CreateClient("Webhooks");

            var deliveries = subscriptions.Select(subscription =>
                SendAsync(subscription, client, webhookEvent.EventId, webhookEvent.EventType, jsonPayload));
            await Task.WhenAll(deliveries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch webhook event {EventId} for tenant {TenantId}",
                webhookEvent.EventId, webhookEvent.TenantId);
        }
    }

    private async Task SendAsync(
        WebhookSubscription subscription,
        HttpClient client,
        Guid eventId,
        string eventType,
        string jsonPayload)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Url)
            {
                Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Inventory-Event", eventType);
            request.Headers.Add("X-Inventory-Event-Id", eventId.ToString("D"));

            if (!string.IsNullOrEmpty(subscription.Secret))
            {
                var keyBytes = Encoding.UTF8.GetBytes(subscription.Secret);
                var payloadBytes = Encoding.UTF8.GetBytes(jsonPayload);
                using var hmac = new HMACSHA256(keyBytes);
                var hash = hmac.ComputeHash(payloadBytes);
                var signature = Convert.ToHexString(hash).ToLowerInvariant();
                request.Headers.Add("X-Inventory-Signature", signature);
            }

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Webhook target {Url} returned status code {StatusCode}", subscription.Url, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch webhook to {Url}", subscription.Url);
        }
    }
}
