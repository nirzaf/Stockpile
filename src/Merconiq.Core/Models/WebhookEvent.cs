using Merconiq.Core.Interfaces;

namespace Merconiq.Core.Models;

/// <summary>Tenant-aware immutable envelope for an outbound webhook event.</summary>
public sealed record WebhookEvent<T>(
    Guid EventId,
    string TenantId,
    string EventType,
    DateTimeOffset Timestamp,
    T Payload);

public static class WebhookEventFactory
{
    public static WebhookEvent<T> Create<T>(ITenantContext tenantContext, string eventType, T payload)
    {
        if (!tenantContext.IsResolved)
        {
            throw new InvalidOperationException("A tenant context is required to create a webhook event.");
        }

        return new WebhookEvent<T>(
            Guid.NewGuid(),
            tenantContext.TenantId,
            eventType,
            DateTimeOffset.UtcNow,
            payload);
    }
}
