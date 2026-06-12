using InventoryManagementSystem.Core.Entities;

namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>Service contract for managing storage locations.</summary>
public interface ILocationService
{
    /// <summary>Retrieves all locations.</summary>
    /// <returns>A collection of locations.</returns>
    Task<IEnumerable<Location>> GetAllAsync();

    /// <summary>Gets a location by its identifier.</summary>
    /// <param name="id">The location identifier.</param>
    /// <returns>The location, or <see langword="null"/> if not found.</returns>
    Task<Location?> GetByIdAsync(int id);

    /// <summary>Creates a new location.</summary>
    /// <param name="location">The location to create.</param>
    /// <returns>The created location with its assigned identifier.</returns>
    Task<Location> CreateAsync(Location location);

    /// <summary>Updates an existing location.</summary>
    /// <param name="location">The location with updated values.</param>
    Task UpdateAsync(Location location);

    /// <summary>Soft-deletes a location by its identifier.</summary>
    /// <param name="id">The identifier of the location to delete.</param>
    Task DeleteAsync(int id);
}
