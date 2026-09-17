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
}
