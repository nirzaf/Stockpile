using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Services;

/// <summary>
/// Stock service. Wraps <see cref="StockInHand"/> and <see cref="StockTransaction"/>
/// persistence with PostgreSQL <c>xmin</c> optimistic concurrency retries and webhook
/// notifications for every movement.
/// </summary>
public class StockService : IStockService
{
    private readonly IRepository<StockInHand> _stockRepo;
    private readonly IRepository<StockTransaction> _txRepo;
    private readonly IRepository<Item> _itemRepo;
    private readonly IRepository<Location> _locationRepo;
    private readonly IRepository<Branch> _branchRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<StockService> _logger;
    private readonly IRepository<StockValuationBucket> _valuationBucketRepo;
    private readonly IRepository<StockValuationEntry> _valuationEntryRepo;

    public StockService(
        IRepository<StockInHand> stockRepo,
        IRepository<StockTransaction> txRepo,
        IRepository<Item> itemRepo,
        IRepository<Location> locationRepo,
        IRepository<Branch> branchRepo,
        IUnitOfWork unitOfWork,
        IWebhookDispatcher webhookDispatcher,
        ITenantContext tenantContext,
        ILogger<StockService> logger,
        IRepository<StockValuationBucket> valuationBucketRepo,
        IRepository<StockValuationEntry> valuationEntryRepo)
    {
        _stockRepo = stockRepo;
        _txRepo = txRepo;
        _itemRepo = itemRepo;
        _locationRepo = locationRepo;
        _branchRepo = branchRepo;
        _unitOfWork = unitOfWork;
        _webhookDispatcher = webhookDispatcher;
        _tenantContext = tenantContext;
        _logger = logger;
        _valuationBucketRepo = valuationBucketRepo ?? throw new ArgumentNullException(nameof(valuationBucketRepo));
        _valuationEntryRepo = valuationEntryRepo ?? throw new ArgumentNullException(nameof(valuationEntryRepo));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StockInHand>> GetAllAsync() => await _stockRepo.GetAllAsync();

    /// <inheritdoc />
    public async Task<IEnumerable<StockInHand>> GetForCompaniesAsync(IReadOnlyCollection<int> companyIds)
    {
        if (companyIds.Count == 0)
        {
            return [];
        }

        return await _stockRepo.FindAsync(stock =>
            stock.Location.Branch != null && companyIds.Contains(stock.Location.Branch.CompanyId));
    }

    /// <inheritdoc />
    public async Task<StockInHand?> GetByItemAndLocationAsync(
        int itemId,
        int locationId,
        string? batchNumber = null,
        DateTime? expiryDate = null)
    {
        var results = await _stockRepo.FindAsync(s =>
            s.ItemId == itemId &&
            s.LocationId == locationId &&
            s.BatchNumber == batchNumber &&
            s.ExpiryDate == expiryDate);
        return results.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StockTransaction>> GetTransactionsAsync(DateTime? from, DateTime? to)
    {
        // Single composite predicate — fully executed on the database, zero in-memory filtering
        return await _txRepo.FindAsync(t =>
            (!from.HasValue || t.TransactionDate >= from.Value) &&
            (!to.HasValue || t.TransactionDate <= to.Value),
            orderBy: q => q.OrderByDescending(t => t.TransactionDate));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StockTransaction>> GetTransactionsForCompaniesAsync(
        DateTime? from,
        DateTime? to,
        IReadOnlyCollection<int> companyIds)
    {
        if (companyIds.Count == 0)
        {
            return [];
        }

        return await _txRepo.FindAsync(t =>
                (!from.HasValue || t.TransactionDate >= from.Value) &&
                (!to.HasValue || t.TransactionDate <= to.Value) &&
                t.FromLocation.Branch != null &&
                companyIds.Contains(t.FromLocation.Branch.CompanyId) &&
                (t.ToLocationId == null ||
                 (t.ToLocation != null &&
                  t.ToLocation.Branch != null &&
                  t.FromLocation.Branch.CompanyId == t.ToLocation.Branch.CompanyId)),
            orderBy: q => q.OrderByDescending(t => t.TransactionDate));
    }

    private async Task ExecuteWithRetryAsync(
        int itemId,
        Func<Task> action,
        Func<Task<bool>> verifySucceeded)
    {
        // PostgreSQL surfaces an optimistic-concurrency conflict as a DbUpdateConcurrencyException
        // (driven by the StockInHand.xmin token). Three retries matches the default
        // EnableRetryOnFailure(3) budget from Program.cs so callers get a single, coherent
        // retry envelope across the system.
        int retries = 3;
        while (true)
        {
            var ownsTransaction = !_unitOfWork.HasActiveTransaction;
            try
            {
                await _unitOfWork.ExecuteInTransactionAsync(async () =>
                {
                    var item = await _itemRepo.GetByIdAsync(itemId);
                    if (item is not null && !item.IsActive)
                        throw new InvalidOperationException("Inactive items cannot be used in stock operations.");
                    await action();
                    if (item is not null)
                        await CheckLowStockAsync(item);
                }, CancellationToken.None, verifySucceeded);
                break;
            }
            catch (Merconiq.Core.Exceptions.ConcurrencyException ex)
            {
                if (!ownsTransaction)
                {
                    // A keyed request is already inside IdempotencyKeyStore's
                    // retryable outer boundary. Restarting here would reuse the
                    // invalid transaction; the coordinator catches this exception
                    // and reruns the complete movement and claim together.
                    throw;
                }
                await _unitOfWork.RollbackTransactionAsync(CancellationToken.None);
                if (--retries <= 0)
                {
                    _logger.LogError(ex, "Concurrency conflict could not be resolved after retries.");
                    throw;
                }
                _logger.LogWarning("Concurrency conflict detected, retrying operation. Retries remaining: {Retries}", retries);
                InventoryTelemetry.ConcurrencyRetries.Add(1);

                // The change tracker is stale after a failed SaveChangesAsync — without
                // clearing, the next attempt would re-attach the now-divergent original
                // values and immediately collide with itself.
                _unitOfWork.ClearTracker();

                // 100 ms backoff: sub-second margin so we don't pile on the database
                // while still being fast enough for interactive UIs.
                await Task.Delay(100);
            }
            catch
            {
                if (ownsTransaction)
                {
                    await _unitOfWork.RollbackTransactionAsync(CancellationToken.None);
                }
                throw;
            }
        }
    }

    /// <inheritdoc />
    public async Task ReceiveStockAsync(
        int itemId,
        int locationId,
        int quantity,
        string? notes,
        string? batchNumber = null,
        DateTime? expiryDate = null,
        decimal? unitCost = null)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive");
        if (unitCost is < 0) throw new ArgumentException("Unit cost must be non-negative");
        if (unitCost.HasValue && (batchNumber is not null || expiryDate.HasValue))
            throw new InvalidOperationException("Valuation is scoped to unbatched stock.");

        StockTransaction? transaction = null;
        await ExecuteWithRetryAsync(itemId, async () =>
        {
            await EnsureLocationUsableAsync(locationId);
            var existing = await GetByItemAndLocationAsync(itemId, locationId, batchNumber, expiryDate);
            if (existing != null)
            {
                existing.Quantity += quantity;
                await _stockRepo.UpdateAsync(existing);
            }
            else
            {
                await _stockRepo.AddAsync(new StockInHand
                {
                    ItemId = itemId,
                    LocationId = locationId,
                    Quantity = quantity,
                    BatchNumber = batchNumber,
                    ExpiryDate = expiryDate
                });
            }

            transaction = new StockTransaction
            {
                ItemId = itemId,
                FromLocationId = locationId,
                ToLocationId = locationId,
                Quantity = quantity,
                TransactionType = TransactionType.Receive,
                TransactionDate = DateTime.UtcNow,
                BatchNumber = batchNumber,
                ExpiryDate = expiryDate,
                Notes = notes
            };
            await _txRepo.AddAsync(transaction);

            if (unitCost is decimal incomingCost)
            {
                await ApplyReceiptValuationAsync(itemId, locationId, quantity, incomingCost, transaction);
            }

            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext, "Stock.Received",
                new { ItemId = itemId, LocationId = locationId, Quantity = quantity, Notes = notes, BatchNumber = batchNumber, ExpiryDate = expiryDate }));
            await _unitOfWork.SaveChangesAsync();
        }, () => VerifyTransactionCommitAsync(transaction));

        _logger.LogInformation("Received {Qty} of item {ItemId} at location {LocId}", quantity, itemId, locationId);
    }

    /// <inheritdoc />
    public async Task TransferStockAsync(int itemId, int fromLocationId, int toLocationId, int quantity, string? notes, string? batchNumber = null, DateTime? expiryDate = null)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive");
        if (fromLocationId == toLocationId) throw new ArgumentException("Source and destination must be different");

        StockTransaction? transaction = null;
        await ExecuteWithRetryAsync(itemId, async () =>
        {
            var sourceLocation = await EnsureLocationUsableAsync(fromLocationId);
            var destinationLocation = await EnsureLocationUsableAsync(toLocationId);
            await EnsureSameCompanyTransferAsync(sourceLocation, destinationLocation);
            var source = await GetByItemAndLocationAsync(itemId, fromLocationId, batchNumber, expiryDate);
            if (source == null || source.Quantity < quantity)
                throw new InvalidOperationException("Insufficient stock at source location");

            source.Quantity -= quantity;
            await _stockRepo.UpdateAsync(source);

            var dest = await GetByItemAndLocationAsync(itemId, toLocationId, batchNumber, expiryDate);
            if (dest != null)
            {
                dest.Quantity += quantity;
                await _stockRepo.UpdateAsync(dest);
            }
            else
            {
                await _stockRepo.AddAsync(new StockInHand
                {
                    ItemId = itemId,
                    LocationId = toLocationId,
                    Quantity = quantity,
                    BatchNumber = batchNumber,
                    ExpiryDate = expiryDate
                });
            }

            transaction = new StockTransaction
            {
                ItemId = itemId,
                FromLocationId = fromLocationId,
                ToLocationId = toLocationId,
                Quantity = quantity,
                TransactionType = TransactionType.Transfer,
                TransactionDate = DateTime.UtcNow,
                BatchNumber = batchNumber,
                ExpiryDate = expiryDate,
                Notes = notes
            };
            await _txRepo.AddAsync(transaction);

            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext, "Stock.Transferred",
                new { ItemId = itemId, FromLocationId = fromLocationId, ToLocationId = toLocationId, Quantity = quantity, Notes = notes, BatchNumber = batchNumber, ExpiryDate = expiryDate }));
            await _unitOfWork.SaveChangesAsync();
        }, () => VerifyTransactionCommitAsync(transaction));

        _logger.LogInformation("Transferred {Qty} of item {ItemId} from {From} to {To}", quantity, itemId, fromLocationId, toLocationId);
    }

    /// <inheritdoc />
    public async Task SellStockAsync(int itemId, int locationId, int quantity, string? notes, string? batchNumber = null, DateTime? expiryDate = null)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive");

        StockTransaction? transaction = null;
        await ExecuteWithRetryAsync(itemId, async () =>
        {
            await EnsureLocationUsableAsync(locationId);
            var stock = await GetByItemAndLocationAsync(itemId, locationId, batchNumber, expiryDate);
            if (stock == null || stock.Quantity < quantity)
                throw new InvalidOperationException("Insufficient stock for sale");

            stock.Quantity -= quantity;
            await _stockRepo.UpdateAsync(stock);

            transaction = new StockTransaction
            {
                ItemId = itemId,
                FromLocationId = locationId,
                Quantity = quantity,
                TransactionType = TransactionType.Sell,
                TransactionDate = DateTime.UtcNow,
                BatchNumber = batchNumber,
                ExpiryDate = expiryDate,
                Notes = notes
            };
            await _txRepo.AddAsync(transaction);

            if (batchNumber is null && expiryDate is null)
            {
                await ApplySaleValuationAsync(itemId, locationId, quantity, transaction);
            }

            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext, "Stock.Sold",
                new { ItemId = itemId, LocationId = locationId, Quantity = quantity, Notes = notes, BatchNumber = batchNumber, ExpiryDate = expiryDate }));
            await _unitOfWork.SaveChangesAsync();
        }, () => VerifyTransactionCommitAsync(transaction));

        _logger.LogInformation("Sold {Qty} of item {ItemId} from location {LocId}", quantity, itemId, locationId);
    }

    private async Task ApplyReceiptValuationAsync(
        int itemId,
        int locationId,
        int quantity,
        decimal unitCost,
        StockTransaction source)
    {
        var existing = (await _valuationBucketRepo!.FindAsync(bucket =>
            bucket.ItemId == itemId && bucket.LocationId == locationId)).FirstOrDefault();
        var totalValue = Round(quantity * unitCost);

        if (existing is null)
        {
            await _valuationBucketRepo.AddAsync(new StockValuationBucket
            {
                ItemId = itemId,
                LocationId = locationId,
                Quantity = quantity,
                Value = totalValue
            });
        }
        else
        {
            var bucket = await _valuationBucketRepo.GetByIdAsync(existing.Id)
                ?? throw new InvalidOperationException("Valuation bucket disappeared during posting.");
            bucket.Quantity = checked(bucket.Quantity + quantity);
            bucket.Value = Round(bucket.Value + totalValue);
            await _valuationBucketRepo.UpdateAsync(bucket);
        }

        await _valuationEntryRepo!.AddAsync(new StockValuationEntry
        {
            StockTransaction = source,
            ItemId = itemId,
            LocationId = locationId,
            EntryType = StockValuationEntryType.Receipt,
            Quantity = quantity,
            UnitCost = Round(unitCost),
            TotalValue = totalValue
        });
    }

    private async Task ApplySaleValuationAsync(
        int itemId,
        int locationId,
        int quantity,
        StockTransaction source)
    {
        // A sale with no bucket remains explicitly unvalued. The required repositories
        // ensure an existing bucket is always consulted instead of silently bypassed.
        var existing = (await _valuationBucketRepo.FindAsync(bucket =>
            bucket.ItemId == itemId && bucket.LocationId == locationId)).FirstOrDefault();
        if (existing is null || existing.Quantity == 0)
        {
            return;
        }

        if (existing.Quantity < quantity)
        {
            throw new InvalidOperationException("Valued stock is insufficient for sale.");
        }

        var bucket = await _valuationBucketRepo.GetByIdAsync(existing.Id)
            ?? throw new InvalidOperationException("Valuation bucket disappeared during posting.");
        var totalValue = bucket.Quantity == quantity
            ? bucket.Value
            : Round(bucket.Value * quantity / bucket.Quantity);
        bucket.Quantity -= quantity;
        bucket.Value = bucket.Quantity == 0 ? 0m : Round(bucket.Value - totalValue);
        await _valuationBucketRepo.UpdateAsync(bucket);

        await _valuationEntryRepo.AddAsync(new StockValuationEntry
        {
            StockTransaction = source,
            ItemId = itemId,
            LocationId = locationId,
            EntryType = StockValuationEntryType.Sale,
            Quantity = quantity,
            UnitCost = Round(totalValue / quantity),
            TotalValue = totalValue
        });
    }

    private static decimal Round(decimal value) => decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private async Task CheckLowStockAsync(Item item)
    {
        try
        {
            var stockInHands = await _stockRepo.FindAsync(s => s.ItemId == item.Id);
            var totalStock = stockInHands.Sum(s => s.Quantity);

            if (totalStock <= item.ReorderLevel)
            {
                _logger.LogWarning("Low stock alert for item {ItemCode}: Total Stock is {TotalStock}, Reorder Level is {ReorderLevel}",
                    item.ItemCode, totalStock, item.ReorderLevel);
                InventoryTelemetry.LowStockAlerts.Add(1);
                await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext, "Stock.Low", new
                {
                    ItemId = item.Id,
                    ItemCode = item.ItemCode,
                    TotalStock = totalStock,
                    ReorderLevel = item.ReorderLevel
                }));
                // EnqueueAsync adds durable delivery rows to this scoped context. Persist
                // them after the movement save so they survive request completion.
                await _unitOfWork.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking low stock level for item {ItemId}", item.Id);
            throw;
        }
    }

    private async Task<Location> EnsureLocationUsableAsync(int locationId)
    {
        if (!_tenantContext.IsResolved)
            throw new InvalidOperationException("A tenant context is required for stock operations.");

        // Filtered queries apply tenant and soft-delete predicates even when this DbContext
        // has already tracked an entity with the requested key.
        var location = (await _locationRepo.FindAsync(candidate => candidate.Id == locationId))
            .FirstOrDefault();
        if (location is null || location.IsDeleted ||
            !string.Equals(location.TenantId, _tenantContext.TenantId, StringComparison.Ordinal))
            throw new InvalidOperationException("Location does not exist in the current tenant or is deleted.");

        if (location.BranchId is not int branchId)
            return location;

        var branch = (await _branchRepo.FindAsync(candidate => candidate.Id == branchId))
            .FirstOrDefault();
        if (branch is null ||
            !string.Equals(branch.TenantId, _tenantContext.TenantId, StringComparison.Ordinal) ||
            !string.Equals(branch.TenantId, location.TenantId, StringComparison.Ordinal))
            throw new InvalidOperationException("Location ownership is not valid for the current tenant.");
        if (!branch.IsActive)
            throw new InvalidOperationException("Locations owned by inactive branches cannot be used in stock operations.");

        location.Branch = branch;
        return location;
    }

    private Task EnsureSameCompanyTransferAsync(Location source, Location destination)
    {
        if (source.BranchId is null || destination.BranchId is null)
            throw new InvalidOperationException("Both locations must be assigned to an active company before stock can be transferred.");

        var sourceBranch = source.Branch
            ?? throw new InvalidOperationException("Location ownership is not valid for the current tenant.");
        var destinationBranch = destination.Branch
            ?? throw new InvalidOperationException("Location ownership is not valid for the current tenant.");
        if (sourceBranch.CompanyId != destinationBranch.CompanyId)
            throw new InvalidOperationException("Cross-company stock transfers are not supported.");

        return Task.CompletedTask;
    }

    private async Task<bool> VerifyTransactionCommitAsync(StockTransaction? transaction)
    {
        if (transaction is null || transaction.Id == 0)
        {
            _unitOfWork.ClearTracker();
            return false;
        }

        var exists = (await _txRepo.FindAsync(item => item.Id == transaction.Id)).Any();
        if (!exists)
        {
            // SaveChanges has already accepted the first attempt's entity state. A
            // false verification means that attempt was rolled back, so detach all
            // stale instances before the execution strategy replays the operation.
            _unitOfWork.ClearTracker();
        }

        return exists;
    }
}
