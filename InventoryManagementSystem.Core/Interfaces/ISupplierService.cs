using InventoryManagementSystem.Core.Entities;

namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>Service contract for managing suppliers.</summary>
public interface ISupplierService
{
    /// <summary>Retrieves all suppliers.</summary>
    /// <returns>A collection of suppliers.</returns>
    Task<IEnumerable<Supplier>> GetAllAsync();

    /// <summary>Gets a supplier by its identifier.</summary>
    /// <param name="id">The supplier identifier.</param>
    /// <returns>The supplier, or <see langword="null"/> if not found.</returns>
    Task<Supplier?> GetByIdAsync(int id);

    /// <summary>Creates a new supplier.</summary>
    /// <param name="supplier">The supplier to create.</param>
    /// <returns>The created supplier with its assigned identifier.</returns>
    Task<Supplier> CreateAsync(Supplier supplier);

    /// <summary>Updates an existing supplier.</summary>
    /// <param name="supplier">The supplier with updated values.</param>
    Task UpdateAsync(Supplier supplier);

    /// <summary>Soft-deletes a supplier by its identifier.</summary>
    /// <param name="id">The identifier of the supplier to delete.</param>
    Task DeleteAsync(int id);
}
