namespace Merconiq.Infrastructure.Services;

/// <summary>Reserved identifiers for units whose original source-system IDs are unknown.</summary>
internal static class LegacyUnitExternalId
{
    public const string ReservedPrefix = "__merconiq_legacy_unmapped_unit__:";
    public const string InitialMigrationId = "20260916180000_AddUnitExternalId";
    public const string ForwardMigrationId = "20260918020000_BackfillLegacyUnitExternalIds";
    public const string ProvenanceTable = "UnitExternalIdBackfillProvenance";
}
