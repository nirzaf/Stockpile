namespace InventoryManagementSystem.Core.Entities;

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
}
