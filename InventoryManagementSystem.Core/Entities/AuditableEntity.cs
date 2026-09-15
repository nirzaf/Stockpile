namespace InventoryManagementSystem.Core.Entities;

/// <summary>
/// Base class for entities that track creation and modification audit information.
/// Fields are auto-populated by InventoryDbContext.SaveChangesAsync.
/// </summary>
public abstract class AuditableEntity : Interfaces.ITenantScoped
{
    public string TenantId { get; set; } = "default";
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
