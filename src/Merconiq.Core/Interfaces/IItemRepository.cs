using Merconiq.Core.Entities;

namespace Merconiq.Core.Interfaces;

/// <summary>Repository operations specific to the item catalog.</summary>
public interface IItemRepository : IRepository<Item>
{
    /// <summary>Searches item fields using the data store's case-insensitive search operator.</summary>
    /// <param name="searchTerm">A validated, trimmed search term.</param>
    /// <returns>Items whose code, description, or barcode contains the term.</returns>
    Task<IEnumerable<Item>> SearchAsync(string searchTerm);
}
