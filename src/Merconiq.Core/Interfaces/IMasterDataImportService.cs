using Merconiq.Core.Models;

namespace Merconiq.Core.Interfaces;

public interface IMasterDataImportService
{
    Task<ImportUnitsResult> ImportUnitsAsync(ImportUnitsRequest request, CancellationToken cancellationToken = default);
    Task<ImportItemsResult> ImportItemsAsync(ImportItemsRequest request, CancellationToken cancellationToken = default);
    Task<ImportCompaniesResult> ImportCompaniesAsync(ImportCompaniesRequest request, CancellationToken cancellationToken = default);
    Task<ImportBranchesResult> ImportBranchesAsync(ImportBranchesRequest request, CancellationToken cancellationToken = default);
    Task<ImportLocationsResult> ImportLocationsAsync(ImportLocationsRequest request, CancellationToken cancellationToken = default);
    Task<ImportSuppliersResult> ImportSuppliersAsync(ImportSuppliersRequest request, CancellationToken cancellationToken = default);
}
