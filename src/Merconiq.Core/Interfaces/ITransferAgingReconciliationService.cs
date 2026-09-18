using Merconiq.Core.Models;

namespace Merconiq.Core.Interfaces;

/// <summary>Reads tenant- and authorized-company-scoped transfer aging and conservation evidence.</summary>
public interface ITransferAgingReconciliationService
{
    /// <summary>Returns a keyset-paginated page of transfer-order lines for the supplied authorized companies.</summary>
    Task<TransferAgingReconciliationPage> GetPageAsync(
        IReadOnlyCollection<int> authorizedCompanyIds,
        int? afterLineId,
        int pageSize,
        CancellationToken cancellationToken = default);
}
