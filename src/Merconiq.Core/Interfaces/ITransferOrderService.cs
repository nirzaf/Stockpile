using Merconiq.Core.Models;

namespace Merconiq.Core.Interfaces;

/// <summary>Manages controlled same-company transfer orders and their source reservations.</summary>
public interface ITransferOrderService
{
    Task<TransferOrderView?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

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
}
