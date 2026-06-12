using InventoryManagementSystem.Core.Interfaces;

namespace InventoryManagementSystem.Core.Entities;

/// <summary>
/// Represents a supplier of catalog items. Maps to the <c>Suppliers</c> table.
/// </summary>
public class Supplier : AuditableEntity, ISoftDelete
{
    public int Id { get; set; }

    /// <summary>Supplier's display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional primary contact name.</summary>
    public string? ContactPerson { get; set; }

    /// <summary>Optional phone number.</summary>
    public string? Phone { get; set; }

    /// <summary>Optional email address.</summary>
    public string? Email { get; set; }

    /// <summary>Optional postal address.</summary>
    public string? Address { get; set; }

    /// <summary>Soft-delete flag. When <see langword="true"/>, the supplier is excluded from default queries.</summary>
    public bool IsDeleted { get; set; }

    public ICollection<Item> Items { get; set; } = new List<Item>();
    public ICollection<PurchaseOrder> PurchaseOrders { get; set; } = new List<PurchaseOrder>();
}
