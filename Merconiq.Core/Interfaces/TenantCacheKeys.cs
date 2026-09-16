namespace Merconiq.Core.Interfaces;

/// <summary>Builds namespaced keys for tenant-dependent in-memory values.</summary>
public static class TenantCacheKeys
{
    public static string AllItems(string tenantId) => Build(tenantId, "items:all");

    public static string ForecastForItem(string tenantId, int itemId, int horizonDays) =>
        Build(tenantId, $"forecast:item:{itemId}:horizon:{horizonDays}");

    public static string ForecastForAllItems(string tenantId, int horizonDays) =>
        Build(tenantId, $"forecast:all:horizon:{horizonDays}");

    private static string Build(string tenantId, string key)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("A tenant identifier is required.", nameof(tenantId));
        }

        return $"tenant:{tenantId}:{key}";
    }
}
