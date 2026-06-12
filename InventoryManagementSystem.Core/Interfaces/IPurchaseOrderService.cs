using InventoryManagementSystem.Core.Entities;

namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>
/// Service contract for purchase order management. Purchase orders transition through the
/// statuses <c>Draft</c>, <c>Submitted</c>, <c>Approved</c>, <c>Received</c>, and <c>Cancelled</c>.
/// </summary>
public interface IPurchaseOrderService
{
    /// <summary>Retrieves all purchase orders.</summary>
    /// <returns>A collection of purchase orders.</returns>
    Task<IEnumerable<PurchaseOrder>> GetAllAsync();

    /// <summary>Retrieves a page of purchase orders.</summary>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="pageSize">The number of purchase orders per page.</param>
    /// <returns>A collection of purchase orders for the requested page.</returns>
    Task<IEnumerable<PurchaseOrder>> GetPagedAsync(int page, int pageSize);

    /// <summary>Gets the total number of purchase orders.</summary>
    /// <returns>The total count.</returns>
    Task<int> GetCountAsync();

    /// <summary>Gets a purchase order with its line items by identifier.</summary>
    /// <param name="id">The purchase order identifier.</param>
    /// <returns>The purchase order, or <see langword="null"/> if not found.</returns>
    Task<PurchaseOrder?> GetByIdAsync(int id);

    /// <summary>Creates a new purchase order with its line items.</summary>
    /// <param name="purchaseOrder">The purchase order header.</param>
    /// <param name="details">The purchase order line items.</param>
    /// <returns>The created purchase order, including its assigned identifier and any computed totals.</returns>
    Task<PurchaseOrder> CreateAsync(PurchaseOrder purchaseOrder, List<OrderDetail> details);

    /// <summary>Transitions a purchase order to a new status.</summary>
    /// <param name="id">The purchase order identifier.</param>
    /// <param name="status">The new status. See <see cref="PurchaseOrderStatus"/> for valid values.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="status"/> is not a known purchase order status.</exception>
    Task UpdateStatusAsync(int id, string status);

    /// <summary>Soft-deletes a purchase order by its identifier.</summary>
    /// <param name="id">The identifier of the purchase order to delete.</param>
    Task DeleteAsync(int id);
}
