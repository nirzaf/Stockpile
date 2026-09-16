using Merconiq.Core.Models;

namespace Merconiq.Core.Interfaces;

public interface IOpeningStockImportService
{
    Task<OpeningStockPreviewResult> PreviewAsync(
        OpeningStockPreviewRequest request,
        CancellationToken cancellationToken = default);

    Task<OpeningStockReplayResult> ReplayAsync(
        OpeningStockReplayRequest request,
        CancellationToken cancellationToken = default);
}
