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
        await ValidateAndNormalizeAsync(item);
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
        await ValidateAndNormalizeAsync(item);
        await ValidateUnitReferencesAsync(item);
        var duplicateCode = await _repo.FindAsync(candidate =>
            candidate.Id != item.Id && candidate.ItemCode == item.ItemCode);
        if (duplicateCode.Any())
            throw new InvalidOperationException("An item with this code already exists for this tenant.");
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

    private async Task ValidateAndNormalizeAsync(Item item)
    {
        item.ItemCode = NormalizeRequired(item.ItemCode, "Item code", 50);
        item.Description = NormalizeRequired(item.Description, "Description", 500);
        item.Barcode = NormalizeOptional(item.Barcode, 100);
        if (item.Rate <= 0)
            throw new ArgumentException("Rate must be positive.", nameof(item));
        ItemQuantityConventions.Validate(item);

        if (item.Barcode is not null)
        {
            var duplicateBarcode = await _repo.FindAsync(candidate =>
                candidate.Id != item.Id && candidate.Barcode == item.Barcode);
            if (duplicateBarcode.Any())
                throw new InvalidOperationException("An item with this barcode already exists for this tenant.");
        }
    }

    private async Task ValidateUnitReferencesAsync(Item item)
    {
        if (item.BaseUnitId is null && (item.PurchaseUnitId.HasValue || item.SalesUnitId.HasValue))
            throw new ArgumentException("A base unit is required when a purchase or sales unit is configured.");
        if (!item.PurchaseUnitId.HasValue && item.PurchaseToBaseFactor != 1m)
            throw new ArgumentException("Purchase-to-base factor must be 1 when no purchase unit is configured.");
        if (!item.SalesUnitId.HasValue && item.SalesToBaseFactor != 1m)
            throw new ArgumentException("Sales-to-base factor must be 1 when no sales unit is configured.");

        var ids = new[] { item.BaseUnitId, item.PurchaseUnitId, item.SalesUnitId }
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
            return;

        var units = (await _unitRepository.FindAsync(unit => ids.Contains(unit.Id))).ToArray();
        if (units.Length != ids.Length || units.Any(unit => unit.IsDeleted))
            throw new ArgumentException("Each item unit must exist and belong to the current tenant.");
        if (units.Any(unit => unit.IsWholeUnitOnly) && item.QuantityPrecision != 0)
            throw new ArgumentException("Items using a whole-unit-only unit must use zero quantity precision.");
    }

    private static string NormalizeRequired(string? value, string name, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maxLength)
            throw new ArgumentException($"{name} is required and must be {maxLength} characters or fewer.", name);
        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.Length <= maxLength
                ? normalized
                : throw new ArgumentException($"Barcode must be {maxLength} characters or fewer.", nameof(value));
    }
}
