using InventoryManagementSystem.Core.Interfaces;

namespace InventoryManagementSystem.Core.Entities;

/// <summary>
/// Represents a physical storage location (warehouse, store, bin, etc.).
/// Maps to the <c>Locations</c> table.
/// </summary>
public class Location : AuditableEntity, ISoftDelete
{
    public int Id { get; set; }

    /// <summary>Display name of the location.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional street address.</summary>
    public string? Address { get; set; }

    /// <summary>Soft-delete flag. When <see langword="true"/>, the location is excluded from default queries.</summary>
    public bool IsDeleted { get; set; }

    public ICollection<StockInHand> StockInHands { get; set; } = new List<StockInHand>();
}
