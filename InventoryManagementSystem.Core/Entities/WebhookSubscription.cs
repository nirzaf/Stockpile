namespace InventoryManagementSystem.Core.Entities;

/// <summary>
/// Represents a subscriber that receives webhook events. Maps to the
/// <c>WebhookSubscriptions</c> table.
/// </summary>
public class WebhookSubscription
{
    public int Id { get; set; }

    /// <summary>HTTPS endpoint that will receive event payloads via POST.</summary>
    public string Url { get; set; } = null!;

    /// <summary>Event name to subscribe to (e.g. <c>Stock.Received</c>, <c>Stock.Transferred</c>, <c>Stock.Sold</c>) or <c>*</c> for all events.</summary>
    public string EventType { get; set; } = null!;

    /// <summary>When <see langword="false"/>, the subscription is skipped during dispatch.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Shared secret used to sign outbound payloads with HMAC-SHA256.</summary>
    public string? Secret { get; set; }
}
