using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;

namespace InventoryManagementSystem.Core.Services;

/// <summary>
/// Item service. Caches the full item list in memory (10-minute TTL) and invalidates
/// the cache on every create / update / delete.
/// </summary>
public class ItemService : IItemService
{
    private readonly IRepository<Item> _repo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ItemService> _logger;
    private readonly IMemoryCache _cache;
    private const string ItemsCacheKey = "all_items";

    public ItemService(IRepository<Item> repo, IUnitOfWork unitOfWork, ILogger<ItemService> logger, IMemoryCache cache)
    {
        _repo = repo;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Item>> GetAllAsync()
    {
        return await _cache.GetOrCreateAsync(ItemsCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await _repo.GetAllAsync();
        }) ?? Array.Empty<Item>();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Item>> GetPagedAsync(int page, int pageSize) => await _repo.GetPagedAsync(page, pageSize);

    /// <inheritdoc />
    public async Task<int> GetCountAsync() => await _repo.CountAsync();

    /// <inheritdoc />
    public async Task<Item?> GetByIdAsync(int id) => await _repo.GetByIdAsync(id);

    /// <inheritdoc />
    public async Task<Item> CreateAsync(Item item)
    {
        _logger.LogInformation("Creating item {ItemCode}", item.ItemCode);
        var created = await _repo.AddAsync(item);
        await _unitOfWork.SaveChangesAsync();
        _cache.Remove(ItemsCacheKey);
        return created;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Item item)
    {
        _logger.LogInformation("Updating item {Id}", item.Id);
        await _repo.UpdateAsync(item);
        await _unitOfWork.SaveChangesAsync();
        _cache.Remove(ItemsCacheKey);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int id)
    {
        var item = await _repo.GetByIdAsync(id);
        if (item != null)
        {
            _logger.LogInformation("Deleting item {Id}", id);
            await _repo.DeleteAsync(item);
            await _unitOfWork.SaveChangesAsync();
            _cache.Remove(ItemsCacheKey);
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Item>> SearchAsync(string searchTerm)
    {
        var term = searchTerm.ToLower();
        return await _repo.FindAsync(i =>
            i.ItemCode.ToLower().Contains(term) ||
            i.Description.ToLower().Contains(term));
    }
}
