namespace Merconiq.Core.Models;

public sealed record OpeningStockPreviewRequest(string Csv);

public sealed record OpeningStockReplayRequest(
    string Csv,
    string ImportReference,
    string ApprovalReference,
    DateTime? CutoverAt = null);

public sealed record OpeningStockReversalRequest(
    string ImportReference,
    string CorrectionReference,
    string ApprovalReference,
    string Reason);

public sealed record OpeningStockRowResult(
    int RowNumber,
    string ExternalReference,
    string Status,
    string? Error = null);

public sealed record OpeningStockPreviewResult(
    int Valid,
    int Rejected,
    IReadOnlyList<OpeningStockRowResult> Rows,
    IReadOnlyList<OpeningStockDiscrepancy>? Discrepancies = null);

public sealed record OpeningStockDiscrepancy(
    int ItemId,
    int LocationId,
    int CurrentQuantity,
    int ApprovedQuantity,
    int Difference);

public sealed record OpeningStockReplayResult(
    string ImportReference,
    int AppliedRows,
    bool AlreadyApplied,
    int Valid,
    int Rejected,
    IReadOnlyList<OpeningStockRowResult> Rows);

public sealed record OpeningStockReversalResult(
    string CorrectionReference,
    int ReversedRows,
    bool AlreadyApplied);
