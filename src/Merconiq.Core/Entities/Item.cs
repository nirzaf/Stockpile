using Merconiq.Core.Interfaces;

namespace Merconiq.Core.Entities;

/// <summary>
/// Represents a catalog item. Maps to the <c>Items</c> table.
/// </summary>
public class Item : AuditableEntity, ISoftDelete
{
    public int Id { get; set; }

    /// <summary>Human-friendly business code (e.g. <c>SKU-001</c>).</summary>
    public string ItemCode { get; set; } = string.Empty;

    /// <summary>Free-text description of the item.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Optional barcode (e.g. EAN-13, UPC-A, Code-128) for scanner-based workflows.</summary>
    public string? Barcode { get; set; }

    /// <summary>Selling rate for the item.</summary>
    public decimal Rate { get; set; }

    /// <summary>Current selling price; historical acquisition cost is stored on postings.</summary>
    public decimal SellingPrice => Rate;

    public int? BaseUnitId { get; set; }
    public UnitOfMeasure? BaseUnit { get; set; }
    public int? PurchaseUnitId { get; set; }
    public UnitOfMeasure? PurchaseUnit { get; set; }
    public int? SalesUnitId { get; set; }
    public UnitOfMeasure? SalesUnit { get; set; }
    public decimal PurchaseToBaseFactor { get; set; } = 1m;
    public decimal SalesToBaseFactor { get; set; } = 1m;
    public int QuantityPrecision { get; set; }
    public bool WholeUnitOnly { get; set; }
    public bool IsActive { get; set; } = true;

    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>Default low-stock threshold (10). Used by replenishment reports.</summary>
    public int ReorderLevel { get; set; } = 10;

    /// <summary>Soft-delete flag. When <see langword="true"/>, the item is excluded from default queries.</summary>
    public bool IsDeleted { get; set; }

    public ICollection<StockInHand> StockInHands { get; set; } = new List<StockInHand>();
    public ICollection<StockTransaction> StockTransactions { get; set; } = new List<StockTransaction>();
    public ICollection<OrderDetail> OrderDetails { get; set; } = new List<OrderDetail>();
}
