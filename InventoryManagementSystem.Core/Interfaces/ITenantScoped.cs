namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>Marks data that belongs to one isolated tenant.</summary>
public interface ITenantScoped
{
    /// <summary>Stable tenant identifier supplied by the authenticated request.</summary>
    string TenantId { get; set; }
}
