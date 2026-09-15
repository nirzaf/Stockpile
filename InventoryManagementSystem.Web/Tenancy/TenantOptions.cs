namespace InventoryManagementSystem.Web.Tenancy;

/// <summary>Trusted host-to-tenant bindings used to resolve a tenant before authentication.</summary>
public sealed class TenantOptions
{
    public const string SectionName = "Tenancy";

    /// <summary>
    /// Exact host names and their tenant identifiers. Forwarded host headers are deliberately
    /// not considered here; a trusted edge must route the request to the mapped host first.
    /// </summary>
    public Dictionary<string, string> HostTenants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
