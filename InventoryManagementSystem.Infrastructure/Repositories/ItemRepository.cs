using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryManagementSystem.Infrastructure.Repositories;

/// <summary>PostgreSQL-aware repository for item catalog searches.</summary>
public sealed class ItemRepository : Repository<Item>, IItemRepository
{
    public ItemRepository(InventoryDbContext context) : base(context) { }

    /// <inheritdoc />
    public async Task<IEnumerable<Item>> SearchAsync(string searchTerm)
    {
        // ILIKE keeps the comparison case-insensitive without wrapping every column in LOWER,
        // allowing PostgreSQL operator classes such as pg_trgm to support this search pattern.
        var escapedTerm = searchTerm
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        var pattern = $"%{escapedTerm}%";

        if (_context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            // The in-memory provider cannot translate PostgreSQL's ILIKE function. This branch
            // keeps the test provider's semantics equivalent without affecting production SQL.
            return _dbSet.AsNoTracking()
                .AsEnumerable()
                .Where(item => item.ItemCode.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
                    || item.Description.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
                    || (item.Barcode?.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        return await _dbSet.AsNoTracking()
            .Where(item => EF.Functions.ILike(item.ItemCode, pattern, "\\")
                || EF.Functions.ILike(item.Description, pattern, "\\")
                || (item.Barcode != null && EF.Functions.ILike(item.Barcode, pattern, "\\")))
            .ToListAsync();
    }
}
