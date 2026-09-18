namespace Merconiq.Core.Entities;

/// <summary>Append-only, source-linked result of posting one physical-count line.</summary>
public sealed class StockCountVariance : AuditableEntity
{
    public int Id { get; set; }
    public int StockCountLineId { get; set; }
    public StockCountLine StockCountLine { get; set; } = null!;
    public int ExpectedCurrentQuantity { get; set; }
    public int CountedQuantity { get; set; }
    public int DeltaQuantity { get; set; }
    public string Reason { get; set; } = string.Empty;
    public decimal? ApprovedUnitCost { get; set; }
    public decimal? ValueAdjustment { get; set; }
    public int? StockTransactionId { get; set; }
    public StockTransaction? StockTransaction { get; set; }
}
