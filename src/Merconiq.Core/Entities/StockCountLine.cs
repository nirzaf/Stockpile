namespace Merconiq.Core.Entities;

/// <summary>Immutable item/lot quantity captured at the start of a physical count.</summary>
public sealed class StockCountLine : AuditableEntity
{
    public int Id { get; set; }
    public int StockCountId { get; set; }
    public StockCount StockCount { get; set; } = null!;
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public string ItemCodeSnapshot { get; set; } = string.Empty;
    public string ItemDescriptionSnapshot { get; set; } = string.Empty;
    public string? BatchNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public int SnapshotQuantity { get; set; }
    public StockCountObservation? Observation { get; set; }
}
