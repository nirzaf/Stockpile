using InventoryManagementSystem.Core.Interfaces;

namespace InventoryManagementSystem.Core.Entities;

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
