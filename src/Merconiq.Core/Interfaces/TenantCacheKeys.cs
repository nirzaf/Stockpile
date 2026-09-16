namespace Merconiq.Core.Interfaces;

/// <summary>Builds namespaced keys for tenant-dependent in-memory values.</summary>
public static class TenantCacheKeys
{
    public static string AllItems(string tenantId) => Build(tenantId, "items:all");

    public static string ForecastForItem(
        string tenantId,
        int itemId,
        int horizonDays,
        IReadOnlyCollection<int>? companyIds = null) =>
        Build(tenantId, $"forecast:item:{itemId}:horizon:{horizonDays}:scope:{CompanyScope(companyIds)}");

    public static string ForecastForAllItems(
        string tenantId,
        int horizonDays,
        IReadOnlyCollection<int>? companyIds = null) =>
        Build(tenantId, $"forecast:all:horizon:{horizonDays}:scope:{CompanyScope(companyIds)}");

    private static string CompanyScope(IReadOnlyCollection<int>? companyIds) =>
        companyIds is null ? "tenant-wide" : string.Join(",", companyIds.Distinct().Order());

    private static string Build(string tenantId, string key)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("A tenant identifier is required.", nameof(tenantId));
        }

        return $"tenant:{tenantId}:{key}";
    }
}
