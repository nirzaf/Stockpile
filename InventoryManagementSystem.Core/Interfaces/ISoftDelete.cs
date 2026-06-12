namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>Marks an entity as soft-deletable. Soft-deleted rows are excluded from default queries.</summary>
public interface ISoftDelete
{
    /// <summary>Gets or sets a value indicating whether the entity has been soft-deleted.</summary>
    bool IsDeleted { get; set; }
}
