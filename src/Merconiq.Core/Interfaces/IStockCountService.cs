using Merconiq.Core.Models;

namespace Merconiq.Core.Interfaces;

/// <summary>Captures and inspects immutable, tenant-scoped physical-count snapshots.</summary>
public interface IStockCountService
{
    Task<StockCountView> StartAsync(
        int locationId,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken = default);

    Task<StockCountView?> GetAsync(
        int countId,
        IReadOnlyCollection<int>? companyIds = null,
        CancellationToken cancellationToken = default);

    Task<StockCountAuthorizationContext?> GetAuthorizationContextAsync(
        int countId,
        CancellationToken cancellationToken = default);

    Task<StockCountLineView?> RecordObservationAsync(
        int countId,
        int lineId,
        int countedQuantity,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken = default);
}
