using Merconiq.Core.Models;

namespace Merconiq.Core.Interfaces;

public interface IMasterDataImportService
{
    Task<ImportUnitsResult> ImportUnitsAsync(ImportUnitsRequest request, CancellationToken cancellationToken = default);
}
