using Merconiq.Core.Entities;
using Merconiq.Core.Models;

namespace Merconiq.Core.Interfaces;

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

    /// <summary>Gets a purchase order and its existing lines for a controlled amendment form.</summary>
    Task<PurchaseOrder?> GetForAmendmentAsync(int id);

    /// <summary>Creates a new purchase order with its line items.</summary>
    /// <param name="purchaseOrder">The purchase order header.</param>
    /// <param name="details">The purchase order line items.</param>
    /// <param name="idempotencyKey">Stable key reused when the same create request is retried.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created purchase order, including its assigned identifier and any computed totals.</returns>
    Task<PurchaseOrder> CreateAsync(
        PurchaseOrder purchaseOrder,
        List<OrderDetail> details,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a permitted generic purchase-order status transition. A purchase order can be
    /// marked received only from a posted goods-receipt source, which this method does not create.
    /// </summary>
    /// <param name="id">The purchase order identifier.</param>
    /// <param name="status">The requested status. Receipt status must come from a posted goods receipt.</param>
    /// <param name="actor">The authenticated identity of the user requesting the transition.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="status"/> is not a known purchase order status.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the requested status transition is not allowed.</exception>
    Task UpdateStatusAsync(int id, string status, PurchaseOrderStatusActor actor);

    /// <summary>Gets the newest recorded purchase-order status changes for an authorized detail view.</summary>
    Task<IReadOnlyList<PurchaseOrderStatusHistoryEntry>> GetStatusHistoryAsync(int id);

    /// <summary>
    /// Applies commercial changes to an approved PO's existing lines. A material change
    /// returns the order to Pending and makes its prior approval version stale.
    /// </summary>
    Task AmendApprovedAsync(int id, PurchaseOrderAmendment amendment);

    /// <summary>Cancels an editable purchase order without deleting its identity or number.</summary>
    /// <param name="id">The identifier of the purchase order to cancel.</param>
    Task DeleteAsync(int id);
}
