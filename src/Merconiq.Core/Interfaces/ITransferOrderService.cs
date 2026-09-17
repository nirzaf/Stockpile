using Merconiq.Core.Models;

namespace Merconiq.Core.Interfaces;

/// <summary>Manages controlled same-company transfer orders and their source reservations.</summary>
public interface ITransferOrderService
{
    Task<TransferOrderView?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Returns the newest 100 transfer orders in the supplied authorized companies.</summary>
    Task<IReadOnlyList<TransferOrderView>> GetRecentForCompaniesAsync(
        IReadOnlyCollection<int> companyIds,
        CancellationToken cancellationToken = default);

    Task<TransferOrderView> CreateAsync(
        CreateTransferOrderRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task AmendAsync(
        int id,
        CreateTransferOrderRequest request,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken = default);

    Task ApproveAsync(
        int id,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        int id,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken = default);

    Task<TransferDispatchView> DispatchAsync(
        int id,
        int lineId,
        int quantity,
        string idempotencyKey,
        string dispatchedBy,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken = default);

    Task<TransferDispatchView?> GetDispatchByKeyAsync(
        int id,
        int lineId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>Accepts part or all of one valued, unbatched dispatch into its destination.</summary>
    Task<TransferTransitReceiptView> ReceiveTransitAsync(
        int id,
        int transitEntryId,
        int quantity,
        string idempotencyKey,
        string receivedBy,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a previously committed receipt for idempotent API replay recovery.</summary>
    Task<TransferTransitReceiptView?> GetReceiptByKeyAsync(
        int id,
        int transitEntryId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}
