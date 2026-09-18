namespace Merconiq.Core.Models;

/// <summary>Authenticated identity responsible for a purchase-order status transition.</summary>
public sealed record PurchaseOrderStatusActor
{
    public PurchaseOrderStatusActor(string userId, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("A stable authenticated user ID is required.", nameof(userId));
        }

        UserId = userId.Trim();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? UserId : displayName.Trim();
    }

    public string UserId { get; }

    public string DisplayName { get; }

    /// <summary>Stores both a readable name and stable identity in the existing audit username field.</summary>
    public string AuditUsername => $"{DisplayName} [{UserId}]";
}
