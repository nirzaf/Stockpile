using System.Net;
using System.Security.Cryptography;
using System.Text;
using Merconiq.Core.Entities;
using Merconiq.Core.Diagnostics;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.Tenancy;
using Merconiq.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Web.BackgroundServices;

/// <summary>Claims and delivers durable webhook records with bounded exponential retry.</summary>
public sealed class WebhookDeliveryBackgroundService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<WebhookDeliveryBackgroundService> logger) : BackgroundService
{
    private const int MaxAttempts = 5;
    internal const int MaximumDiagnosticResponseBytes = 4096;
    private const string TruncatedResponseMarker = "\n[truncated]";

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
        Guid eventId;
        Guid leaseToken;
        string url;
        string secret;
        string eventType;
        string payload;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var now = DateTimeOffset.UtcNow;
            var claim = await WebhookDeliveryLeaseStore.ClaimNextAsync(
                db,
                now,
                TimeSpan.FromMinutes(2),
                cancellationToken);

            if (claim is null)
            {
                return false;
            }

            if (claim.LeaseToken is not Guid claimedLeaseToken ||
                !WebhookDeliveryLeaseStore.IsOwnedBy(claim, claimedLeaseToken))
            {
                throw new InvalidOperationException("The claimed webhook delivery did not receive a valid lease token.");
            }

            if (claim.PayloadByteLength > WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes)
            {
                if (await WebhookDeliveryLeaseStore.TryDeadLetterOversizedAsync(db, claim, now, cancellationToken))
                {
                    InventoryTelemetry.WebhookFailures.Add(1);
                    logger.LogWarning(
                        "Webhook delivery {DeliveryId} was dead-lettered because its payload exceeds {MaximumBytes} UTF-8 bytes.",
                        claim.Id,
                        WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes);
                }

                return true;
            }

            var delivery = await WebhookDeliveryLeaseStore.FindOwnedWithinPayloadLimitAsync(
                db,
                claim.Id,
                claim.TenantId,
                claimedLeaseToken,
                WebhookPayloadPolicy.MaximumSerializedEnvelopeBytes,
                cancellationToken);
            if (delivery is null)
            {
                return true;
            }

            if (!WebhookDeliveryLeaseStore.IsOwnedBy(delivery, claimedLeaseToken))
            {
                throw new InvalidOperationException("The loaded webhook delivery is not owned by the current lease.");
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
                delivery.LeaseUntil = null;
                delivery.LeaseToken = null;
                await TrySaveLeaseOwnerAsync(db, delivery, claimedLeaseToken, cancellationToken);
                return true;
            }

            deliveryId = delivery.Id;
            tenantId = delivery.TenantId;
            eventId = delivery.EventId;
            leaseToken = claimedLeaseToken;
            url = subscription.Url;
            secret = subscription.Secret ?? string.Empty;
            eventType = delivery.EventType;
            payload = delivery.Payload;
        }

        HttpStatusCode? statusCode = null;
        string? responseBody = null;
        string? deliveryError = null;
        try
        {
            deliveryError = await WebhookUrlValidator.ValidateAsync(url, cancellationToken);
            if (deliveryError is null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                request.Headers.Add("X-Inventory-Event", eventType);
                AddEventIdentityHeader(request, eventId);
                if (!string.IsNullOrEmpty(secret))
                {
                    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                    request.Headers.Add("X-Inventory-Signature", Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant());
                }

                var client = httpClientFactory.CreateClient("Webhooks");
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (client.Timeout != Timeout.InfiniteTimeSpan)
                {
                    requestTimeout.CancelAfter(client.Timeout);
                }

                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestTimeout.Token);
                statusCode = response.StatusCode;
                responseBody = await ReadDiagnosticResponseAsync(response.Content, secret, url, requestTimeout.Token);
            }
        }
        catch (HttpRequestException)
        {
            deliveryError = "Webhook transport failed.";
        }
        catch (IOException)
        {
            deliveryError = "Webhook transport failed.";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            deliveryError = "Webhook request timed out.";
        }

        await CompleteAsync(deliveryId, tenantId, leaseToken, statusCode, responseBody, deliveryError, cancellationToken);

        return true;
    }

    internal static void AddEventIdentityHeader(HttpRequestMessage request, Guid eventId)
    {
        request.Headers.Add("X-Inventory-Event-Id", eventId.ToString("D"));
    }

    internal static async Task<string> ReadDiagnosticResponseAsync(
        HttpContent content,
        string? secret,
        string url,
        CancellationToken cancellationToken)
    {
        var maximumBytesToRead = MaximumDiagnosticResponseBytes + 1;
        var buffer = new byte[1024];
        using var responseBytes = new MemoryStream(capacity: maximumBytesToRead);
        await using var responseStream = await content.ReadAsStreamAsync(cancellationToken);

        while (responseBytes.Length < maximumBytesToRead)
        {
            var bytesRemaining = maximumBytesToRead - (int)responseBytes.Length;
            var bytesRead = await responseStream.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, bytesRemaining)),
                cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            responseBytes.Write(buffer, 0, bytesRead);
        }

        var truncated = responseBytes.Length > MaximumDiagnosticResponseBytes;
        var bytesToDecode = responseBytes.ToArray();
        if (truncated)
        {
            var markerBytes = Encoding.UTF8.GetByteCount(TruncatedResponseMarker);
            bytesToDecode = bytesToDecode[..(MaximumDiagnosticResponseBytes - markerBytes)];
        }

        var diagnostic = DecodeUtf8Prefix(bytesToDecode);
        var redactionWindowBytes = Math.Max(
            Encoding.UTF8.GetByteCount(url),
            Encoding.UTF8.GetByteCount(secret ?? string.Empty));
        if (!string.IsNullOrEmpty(url))
        {
            diagnostic = diagnostic.Replace(url, "[url]", StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(secret))
        {
            diagnostic = diagnostic.Replace(secret, new string('*', secret.Length), StringComparison.Ordinal);
        }

        if (truncated)
        {
            var markerBytes = Encoding.UTF8.GetByteCount(TruncatedResponseMarker);
            var maximumSafePrefixBytes = Math.Max(
                0,
                MaximumDiagnosticResponseBytes - markerBytes - redactionWindowBytes);
            diagnostic = TruncateUtf8(diagnostic, maximumSafePrefixBytes);
        }

        return truncated ? diagnostic + TruncatedResponseMarker : diagnostic;
    }

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maximumBytes)
        {
            return value;
        }

        var length = Math.Clamp(maximumBytes, 0, bytes.Length);
        while (length > 0 && (bytes[length] & 0b1100_0000) == 0b1000_0000)
        {
            length--;
        }

        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    private static string DecodeUtf8Prefix(byte[] bytes)
    {
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        for (var length = bytes.Length; length >= Math.Max(0, bytes.Length - 3); length--)
        {
            try
            {
                return strictUtf8.GetString(bytes, 0, length);
            }
            catch (DecoderFallbackException)
            {
                // The response limit may split a multi-byte UTF-8 character. Drop only that partial suffix.
            }
        }

        return string.Empty;
    }

    private async Task CompleteAsync(
        long id,
        string tenantId,
        Guid leaseToken,
        HttpStatusCode? statusCode,
        string? responseBody,
        string? error,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.SetTenant(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var delivery = await WebhookDeliveryLeaseStore.FindOwnedAsync(db, id, tenantId, leaseToken, cancellationToken);
        if (delivery is null)
        {
            logger.LogDebug("Ignoring completion from an expired webhook delivery lease for delivery {DeliveryId}.", id);
            return;
        }

        var successful = error is null && statusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices;
        var completedAt = DateTimeOffset.UtcNow;
        delivery.LastStatusCode = statusCode is null ? null : (int)statusCode;
        delivery.LastResponse = responseBody;
        delivery.LastError = error;
        delivery.LeaseUntil = null;
        delivery.LeaseToken = null;

        if (successful)
        {
            delivery.Status = WebhookDeliveryStatus.Delivered;
            delivery.DeliveredAt = completedAt;
        }
        else if (delivery.AttemptCount >= MaxAttempts || (statusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError && statusCode != HttpStatusCode.RequestTimeout && statusCode != HttpStatusCode.TooManyRequests))
        {
            delivery.Status = WebhookDeliveryStatus.DeadLetter;
        }
        else
        {
            delivery.Status = WebhookDeliveryStatus.Pending;
            delivery.NextAttemptAt = completedAt.AddSeconds(Math.Min(300, 15 * Math.Pow(2, delivery.AttemptCount - 1)));
        }

        if (!await TrySaveLeaseOwnerAsync(db, delivery, leaseToken, cancellationToken))
        {
            return;
        }

        if (delivery.Status == WebhookDeliveryStatus.DeadLetter)
        {
            InventoryTelemetry.WebhookFailures.Add(1);
            logger.LogError("Webhook delivery {DeliveryId} moved to dead letter for tenant {TenantId}: {Error}", id, tenantId, error ?? $"HTTP {(int?)statusCode}");
        }
    }

    private async Task<bool> TrySaveLeaseOwnerAsync(
        InventoryDbContext db,
        WebhookDelivery delivery,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        var originalLeaseToken = db.Entry(delivery).Property(item => item.LeaseToken).OriginalValue;
        if (originalLeaseToken != leaseToken)
        {
            return false;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogDebug("Ignoring completion from a webhook delivery lease superseded for delivery {DeliveryId}.", delivery.Id);
            return false;
        }
    }
}
