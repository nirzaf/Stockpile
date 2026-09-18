namespace Merconiq.Core.Models;

/// <summary>Reserved identities used by controlled master-data migrations.</summary>
public static class MasterDataImportConventions
{
    /// <summary>Prefix for legacy units whose original source-system ID is unknown.</summary>
    public const string LegacyUnmappedUnitExternalIdPrefix = "__merconiq_legacy_unmapped_unit__:";
}

public sealed record ImportUnitsRequest(string Csv, bool DryRun = true, int? CompanyId = null);

public sealed record ImportRowResult(int RowNumber, string ExternalId, string Status, string? Error = null);

public sealed record ImportUnitsResult(bool DryRun, int Created, int Unchanged, int Rejected,
    IReadOnlyList<ImportRowResult> Rows);

public sealed record ImportItemsRequest(string Csv, bool DryRun = true, int? CompanyId = null);

public sealed record ImportItemsResult(bool DryRun, int Created, int Unchanged, int Rejected,
    IReadOnlyList<ImportRowResult> Rows);

public sealed record ImportCompaniesRequest(string Csv, bool DryRun = true);

public sealed record ImportCompaniesResult(bool DryRun, int Created, int Unchanged, int Rejected,
    IReadOnlyList<ImportRowResult> Rows);

public sealed record ImportBranchesRequest(string Csv, bool DryRun = true, int? CompanyId = null);

public sealed record ImportBranchesResult(bool DryRun, int Created, int Unchanged, int Rejected,
    IReadOnlyList<ImportRowResult> Rows);

public sealed record ImportLocationsRequest(string Csv, bool DryRun = true, int? CompanyId = null);

public sealed record ImportLocationsResult(bool DryRun, int Created, int Unchanged, int Rejected,
    IReadOnlyList<ImportRowResult> Rows);

public sealed record ImportSuppliersRequest(string Csv, bool DryRun = true, int? CompanyId = null);

public sealed record ImportSuppliersResult(bool DryRun, int Created, int Unchanged, int Rejected,
    IReadOnlyList<ImportRowResult> Rows);
