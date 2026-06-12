namespace InventoryManagementSystem.Core.Entities;

/// <summary>
/// Lifecycle statuses for purchase orders. Valid transitions:
/// <c>Draft → Pending → Submitted → Approved → Received</c>, with <c>Cancelled</c> available
/// from any non-terminal status.
/// </summary>
public enum PurchaseOrderStatus
{
    /// <summary>Initial state — being prepared, not yet sent to the supplier.</summary>
    Draft,

    /// <summary>Awaiting internal review before submission.</summary>
    Pending,

    /// <summary>Sent to the supplier.</summary>
    Submitted,

    /// <summary>Supplier has confirmed the order.</summary>
    Approved,

    /// <summary>Goods have been received and stock-in-hand updated.</summary>
    Received,

    /// <summary>Order has been cancelled. Terminal state.</summary>
    Cancelled
}
