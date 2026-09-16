namespace Merconiq.Core.Entities;

/// <summary>
/// Represents the current on-hand quantity of an item at a specific location.
/// Maps to the <c>StockInHand</c> table.
/// </summary>
public class StockInHand : AuditableEntity
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public int LocationId { get; set; }
    public Location Location { get; set; } = null!;

    /// <summary>The current quantity of this item at this location.</summary>
    public int Quantity { get; set; }

    /// <summary>PostgreSQL xmin value used for optimistic concurrency checks.</summary>
    public uint Version { get; set; }

    /// <summary>Optional batch or lot identifier for regulated inventory.</summary>
    public string? BatchNumber { get; set; }

    /// <summary>Optional expiry date for the tracked batch or lot.</summary>
    public DateTime? ExpiryDate { get; set; }
}
