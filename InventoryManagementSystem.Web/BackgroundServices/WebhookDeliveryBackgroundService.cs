using System.Net;
using System.Security.Cryptography;
using System.Text;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Diagnostics;
using InventoryManagementSystem.Infrastructure.Data;
using InventoryManagementSystem.Web.Tenancy;
using InventoryManagementSystem.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace InventoryManagementSystem.Web.BackgroundServices;

/// <summary>Claims and delivers durable webhook records with bounded exponential retry.</summary>
public sealed class WebhookDeliveryBackgroundService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookDeliveryBackgroundService> logger) : BackgroundService
{
    private const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            while (await ProcessOneAsync(stoppingToken))
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        long deliveryId;
        string tenantId;
        string url;
        string secret;
        string eventType;
        string payload;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var now = DateTimeOffset.UtcNow;
            var delivery = await db.WebhookDeliveries
                .IgnoreQueryFilters()
                .Where(item =>
                    (item.Status == WebhookDeliveryStatus.Pending && item.NextAttemptAt <= now) ||
                    (item.Status == WebhookDeliveryStatus.InProgress && item.LeaseUntil <= now))
                .OrderBy(item => item.NextAttemptAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (delivery is null)
            {
                return false;
            }

            var subscription = await db.WebhookSubscriptions
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(item => item.Id == delivery.SubscriptionId && item.TenantId == delivery.TenantId, cancellationToken);
            var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
            tenant.SetTenant(delivery.TenantId);
            if (subscription is null || !subscription.IsActive)
            {
                delivery.Status = WebhookDeliveryStatus.DeadLetter;
                delivery.LastError = "Subscription no longer exists or is inactive.";
                delivery.LastAttemptAt = now;
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }

            delivery.Status = WebhookDeliveryStatus.InProgress;
            delivery.AttemptCount++;
            delivery.LastAttemptAt = now;
            delivery.LeaseUntil = now.AddMinutes(2);
            await db.SaveChangesAsync(cancellationToken);

            deliveryId = delivery.Id;
            tenantId = delivery.TenantId;
            url = subscription.Url;
            secret = subscription.Secret ?? string.Empty;
            eventType = delivery.EventType;
            payload = delivery.Payload;
        }

        try
        {
            var validationError = await WebhookUrlValidator.ValidateAsync(url, cancellationToken);
            if (validationError != null)
            {
                await CompleteAsync(deliveryId, tenantId, null, null, validationError, cancellationToken);
                return true;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Inventory-Event", eventType);
            request.Headers.Add("X-Inventory-Event-Id", deliveryId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(secret))
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                request.Headers.Add("X-Inventory-Signature", Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant());
            }

            using var response = await httpClientFactory.CreateClient("Webhooks").SendAsync(request, cancellationToken);
            await CompleteAsync(deliveryId, tenantId, response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken), null, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await CompleteAsync(deliveryId, tenantId, null, null, ex.Message, cancellationToken);
        }

        return true;
    }

    private async Task CompleteAsync(long id, string tenantId, HttpStatusCode? statusCode, string? responseBody, string? error, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var delivery = await db.WebhookDeliveries.IgnoreQueryFilters().SingleAsync(item => item.Id == id, cancellationToken);
        var successful = error is null && statusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices;
        delivery.LastStatusCode = statusCode is null ? null : (int)statusCode;
        delivery.LastResponse = responseBody;
        delivery.LastError = error;
        delivery.LeaseUntil = null;

        if (successful)
        {
            delivery.Status = WebhookDeliveryStatus.Delivered;
            delivery.DeliveredAt = DateTimeOffset.UtcNow;
        }
        else if (delivery.AttemptCount >= MaxAttempts || (statusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError && statusCode != HttpStatusCode.RequestTimeout && statusCode != HttpStatusCode.TooManyRequests))
        {
            delivery.Status = WebhookDeliveryStatus.DeadLetter;
        }
        else
        {
            delivery.Status = WebhookDeliveryStatus.Pending;
            delivery.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 15 * Math.Pow(2, delivery.AttemptCount - 1)));
        }

        await db.SaveChangesAsync(cancellationToken);
        if (delivery.Status == WebhookDeliveryStatus.DeadLetter)
        {
            InventoryTelemetry.WebhookFailures.Add(1);
            logger.LogError("Webhook delivery {DeliveryId} moved to dead letter for tenant {TenantId}: {Error}", id, tenantId, error ?? $"HTTP {(int?)statusCode}");
        }
    }
}
