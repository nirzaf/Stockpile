namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>
/// Identifies the tenant selected for the current request or explicitly-created work scope.
/// </summary>
public interface ITenantContext
{
    /// <summary>Gets the resolved tenant identifier.</summary>
    string TenantId { get; }

    /// <summary>Gets a value indicating whether a tenant has been resolved.</summary>
    bool IsResolved { get; }
}
