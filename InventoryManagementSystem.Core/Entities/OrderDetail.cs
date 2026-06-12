namespace InventoryManagementSystem.Core.Entities;

/// <summary>
/// Represents a single line item on a purchase order. Maps to the <c>OrderDetails</c> table.
/// </summary>
public class OrderDetail : AuditableEntity
{
    public int Id { get; set; }
    public int PurchaseOrderId { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = null!;
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    /// <summary>Quantity of the item ordered on this line.</summary>
    public int Quantity { get; set; }

    /// <summary>Per-unit price agreed with the supplier.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Computed line total: <c>Quantity * UnitPrice</c>.</summary>
    public decimal TotalPrice => Quantity * UnitPrice;
}
