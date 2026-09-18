namespace Merconiq.Core.Entities;

/// <summary>Append-only physical quantity recorded against a count snapshot line.</summary>
public sealed class StockCountObservation : AuditableEntity
{
    public int Id { get; set; }
    public int StockCountLineId { get; set; }
    public StockCountLine StockCountLine { get; set; } = null!;
    public int CountedQuantity { get; set; }
}
