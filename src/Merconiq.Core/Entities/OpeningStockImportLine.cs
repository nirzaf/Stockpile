namespace Merconiq.Core.Entities;

/// <summary>Append-only source line applied by an opening-stock replay.</summary>
public sealed class OpeningStockImportLine : AuditableEntity
{
    public int Id { get; set; }
    public int OpeningStockImportId { get; set; }
    public OpeningStockImport OpeningStockImport { get; set; } = null!;

    public int? StockTransactionId { get; set; }
    public StockTransaction? StockTransaction { get; set; }

    public int RowNumber { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public int LocationId { get; set; }
    public Location Location { get; set; } = null!;
    public int Quantity { get; set; }
    public decimal UnitCost { get; set; }
}
