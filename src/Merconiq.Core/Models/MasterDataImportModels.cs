namespace Merconiq.Core.Models;

public sealed record ImportUnitsRequest(string Csv, bool DryRun = true);

public sealed record ImportRowResult(int RowNumber, string ExternalId, string Status, string? Error = null);

public sealed record ImportUnitsResult(bool DryRun, int Created, int Unchanged, int Rejected,
    IReadOnlyList<ImportRowResult> Rows);

public sealed record ImportItemsRequest(string Csv, bool DryRun = true);

public sealed record ImportItemsResult(bool DryRun, int Created, int Unchanged, int Rejected,
    IReadOnlyList<ImportRowResult> Rows);
