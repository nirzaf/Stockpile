using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;

namespace Merconiq.Core.Services;

/// <summary>
/// Item service. Caches the full item list in memory (10-minute TTL) and invalidates
/// the cache on every create / update / delete.
/// </summary>
public class ItemService : IItemService
{
    private readonly IItemRepository _repo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<ItemService> _logger;
    private readonly IMemoryCache _cache;
    private readonly ITenantContext _tenantContext;
    private readonly IRepository<UnitOfMeasure> _unitRepository;
    private const int MaxSearchTermLength = 100;

    public ItemService(
        IItemRepository repo,
        IUnitOfWork unitOfWork,
        ILogger<ItemService> logger,
        IMemoryCache cache,
        ITenantContext tenantContext,
        IRepository<UnitOfMeasure> unitRepository)
    {
        _repo = repo;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _cache = cache;
        _tenantContext = tenantContext;
        _unitRepository = unitRepository;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Item>> GetAllAsync()
    {
        var cacheKey = TenantCacheKeys.AllItems(_tenantContext.TenantId);
        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
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
        await ValidateUnitReferencesAsync(item);
        var existing = await _repo.FindAsync(candidate => candidate.ItemCode == item.ItemCode);
        if (existing.Any())
        {
            throw new InvalidOperationException("An item with this code already exists for this tenant.");
        }

        var created = await _repo.AddAsync(item);
        await _unitOfWork.SaveChangesAsync();
        _cache.Remove(TenantCacheKeys.AllItems(_tenantContext.TenantId));
        return created;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Item item)
    {
        _logger.LogInformation("Updating item {Id}", item.Id);
        await ValidateUnitReferencesAsync(item);
        await _repo.UpdateAsync(item);
        await _unitOfWork.SaveChangesAsync();
        _cache.Remove(TenantCacheKeys.AllItems(_tenantContext.TenantId));
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
            _cache.Remove(TenantCacheKeys.AllItems(_tenantContext.TenantId));
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<Item>> SearchAsync(string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return Array.Empty<Item>();
        }

        var term = searchTerm.Trim();
        if (term.Length > MaxSearchTermLength)
        {
            throw new ArgumentException(
                $"Search term cannot exceed {MaxSearchTermLength} characters.", nameof(searchTerm));
        }

        return await _repo.SearchAsync(term);
    }

    private async Task ValidateUnitReferencesAsync(Item item)
    {
        var ids = new[] { item.BaseUnitId, item.PurchaseUnitId, item.SalesUnitId }
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        if (ids.Length == 0) return;

        var validIds = (await _unitRepository.FindAsync(unit => ids.Contains(unit.Id)))
            .Select(unit => unit.Id)
            .ToHashSet();
        if (validIds.Count != ids.Length)
            throw new ArgumentException("Each item unit must exist and belong to the current tenant.");
    }
}
