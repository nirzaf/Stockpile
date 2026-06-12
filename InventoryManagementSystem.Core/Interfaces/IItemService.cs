using InventoryManagementSystem.Core.Entities;

namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>Service contract for managing catalog items.</summary>
public interface IItemService
{
    /// <summary>Retrieves all items.</summary>
    /// <returns>A collection of items.</returns>
    Task<IEnumerable<Item>> GetAllAsync();

    /// <summary>Retrieves a page of items.</summary>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <returns>A collection of items for the requested page.</returns>
    Task<IEnumerable<Item>> GetPagedAsync(int page, int pageSize);

    /// <summary>Gets the total number of items.</summary>
    /// <returns>The total item count.</returns>
    Task<int> GetCountAsync();

    /// <summary>Gets an item by its identifier.</summary>
    /// <param name="id">The item identifier.</param>
    /// <returns>The item, or <see langword="null"/> if not found.</returns>
    Task<Item?> GetByIdAsync(int id);

    /// <summary>Creates a new item.</summary>
    /// <param name="item">The item to create.</param>
    /// <returns>The created item, including its assigned identifier.</returns>
    Task<Item> CreateAsync(Item item);

    /// <summary>Updates an existing item.</summary>
    /// <param name="item">The item with updated values.</param>
    Task UpdateAsync(Item item);

    /// <summary>Soft-deletes an item by its identifier.</summary>
    /// <param name="id">The identifier of the item to delete.</param>
    Task DeleteAsync(int id);

    /// <summary>Searches items by a free-text term matched against name, SKU, and barcode.</summary>
    /// <param name="searchTerm">The text to search for.</param>
    /// <returns>Matching items.</returns>
    Task<IEnumerable<Item>> SearchAsync(string searchTerm);
}
