namespace Merconiq.Core.Models;

public sealed record OpeningStockPreviewRequest(string Csv);

public sealed record OpeningStockReplayRequest(
    string Csv,
    string ImportReference,
    string ApprovalReference);

public sealed record OpeningStockRowResult(
    int RowNumber,
    string ExternalReference,
    string Status,
    string? Error = null);

public sealed record OpeningStockPreviewResult(
    int Valid,
    int Rejected,
    IReadOnlyList<OpeningStockRowResult> Rows);

public sealed record OpeningStockReplayResult(
    string ImportReference,
    int AppliedRows,
    bool AlreadyApplied,
    int Valid,
    int Rejected,
    IReadOnlyList<OpeningStockRowResult> Rows);
