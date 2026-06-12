namespace InventoryManagementSystem.Core.Entities;

/// <summary>
/// Represents a purchase order placed with a supplier. Maps to the <c>PurchaseOrders</c> table.
/// </summary>
public class PurchaseOrder : AuditableEntity
{
    public int Id { get; set; }

    /// <summary>Business purchase order number (e.g. <c>PO-2026-0001</c>).</summary>
    public string PONumber { get; set; } = string.Empty;

    /// <summary>Date the purchase order was raised.</summary>
    public DateTime OrderDate { get; set; }

    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    /// <summary>Monetary total of all line items.</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>Current lifecycle status of the purchase order.</summary>
    public PurchaseOrderStatus Status { get; set; }

    /// <summary>Optional free-text notes.</summary>
    public string? Notes { get; set; }

    public ICollection<OrderDetail> OrderDetails { get; set; } = new List<OrderDetail>();
}
