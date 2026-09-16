namespace Merconiq.Core.Entities;

/// <summary>Immutable cost evidence linked to one stock movement.</summary>
public sealed class StockValuationEntry : AuditableEntity
{
    public int Id { get; set; }
    public int StockTransactionId { get; set; }
    public StockTransaction StockTransaction { get; set; } = null!;
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public int LocationId { get; set; }
    public Location Location { get; set; } = null!;
    public StockValuationEntryType EntryType { get; set; }
    public int Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TotalValue { get; set; }
}
