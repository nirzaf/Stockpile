using Merconiq.Core.Interfaces;

namespace Merconiq.Core.Entities;

/// <summary>Durable per-subscription delivery record for an outbound webhook event.</summary>
public sealed class WebhookDelivery : ITenantScoped
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "default";
    public Guid EventId { get; set; }
    public int SubscriptionId { get; set; }
    public string EventType { get; set; } = null!;
    public string Payload { get; set; } = null!;
    public WebhookDeliveryStatus Status { get; set; } = WebhookDeliveryStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public Guid? LeaseToken { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastResponse { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum WebhookDeliveryStatus
{
    Pending,
    InProgress,
    Delivered,
    DeadLetter
}
