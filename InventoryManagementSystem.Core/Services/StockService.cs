using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Core.Models;
using InventoryManagementSystem.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace InventoryManagementSystem.Core.Services;

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
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<StockService> _logger;

    public StockService(
        IRepository<StockInHand> stockRepo,
        IRepository<StockTransaction> txRepo,
        IRepository<Item> itemRepo,
        IUnitOfWork unitOfWork,
        IWebhookDispatcher webhookDispatcher,
        ITenantContext tenantContext,
        ILogger<StockService> logger)
    {
        _stockRepo = stockRepo;
        _txRepo = txRepo;
        _itemRepo = itemRepo;
        _unitOfWork = unitOfWork;
        _webhookDispatcher = webhookDispatcher;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StockInHand>> GetAllAsync() => await _stockRepo.GetAllAsync();

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

    private async Task ExecuteWithRetryAsync(int itemId, Func<Task> action)
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
                    await action();
                    await CheckLowStockAsync(itemId);
                }, CancellationToken.None);
                break;
            }
            catch (InventoryManagementSystem.Core.Exceptions.ConcurrencyException ex)
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
    public async Task ReceiveStockAsync(int itemId, int locationId, int quantity, string? notes, string? batchNumber = null, DateTime? expiryDate = null)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive");

        await ExecuteWithRetryAsync(itemId, async () =>
        {
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

            await _txRepo.AddAsync(new StockTransaction
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
            });

            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext, "Stock.Received",
                new { ItemId = itemId, LocationId = locationId, Quantity = quantity, Notes = notes, BatchNumber = batchNumber, ExpiryDate = expiryDate }));
            await _unitOfWork.SaveChangesAsync();
        });

        _logger.LogInformation("Received {Qty} of item {ItemId} at location {LocId}", quantity, itemId, locationId);
    }

    /// <inheritdoc />
    public async Task TransferStockAsync(int itemId, int fromLocationId, int toLocationId, int quantity, string? notes, string? batchNumber = null, DateTime? expiryDate = null)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive");
        if (fromLocationId == toLocationId) throw new ArgumentException("Source and destination must be different");

        await ExecuteWithRetryAsync(itemId, async () =>
        {
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

            await _txRepo.AddAsync(new StockTransaction
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
            });

            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext, "Stock.Transferred",
                new { ItemId = itemId, FromLocationId = fromLocationId, ToLocationId = toLocationId, Quantity = quantity, Notes = notes, BatchNumber = batchNumber, ExpiryDate = expiryDate }));
            await _unitOfWork.SaveChangesAsync();
        });

        _logger.LogInformation("Transferred {Qty} of item {ItemId} from {From} to {To}", quantity, itemId, fromLocationId, toLocationId);
    }

    /// <inheritdoc />
    public async Task SellStockAsync(int itemId, int locationId, int quantity, string? notes, string? batchNumber = null, DateTime? expiryDate = null)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive");

        await ExecuteWithRetryAsync(itemId, async () =>
        {
            var stock = await GetByItemAndLocationAsync(itemId, locationId, batchNumber, expiryDate);
            if (stock == null || stock.Quantity < quantity)
                throw new InvalidOperationException("Insufficient stock for sale");

            stock.Quantity -= quantity;
            await _stockRepo.UpdateAsync(stock);

            await _txRepo.AddAsync(new StockTransaction
            {
                ItemId = itemId,
                FromLocationId = locationId,
                Quantity = quantity,
                TransactionType = TransactionType.Sell,
                TransactionDate = DateTime.UtcNow,
                BatchNumber = batchNumber,
                ExpiryDate = expiryDate,
                Notes = notes
            });

            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext, "Stock.Sold",
                new { ItemId = itemId, LocationId = locationId, Quantity = quantity, Notes = notes, BatchNumber = batchNumber, ExpiryDate = expiryDate }));
            await _unitOfWork.SaveChangesAsync();
        });

        _logger.LogInformation("Sold {Qty} of item {ItemId} from location {LocId}", quantity, itemId, locationId);
    }

    private async Task CheckLowStockAsync(int itemId)
    {
        try
        {
            var item = await _itemRepo.GetByIdAsync(itemId);
            if (item == null) return;

            var stockInHands = await _stockRepo.FindAsync(s => s.ItemId == itemId);
            var totalStock = stockInHands.Sum(s => s.Quantity);

            if (totalStock <= item.ReorderLevel)
            {
                _logger.LogWarning("Low stock alert for item {ItemCode}: Total Stock is {TotalStock}, Reorder Level is {ReorderLevel}", 
                    item.ItemCode, totalStock, item.ReorderLevel);
                InventoryTelemetry.LowStockAlerts.Add(1);
                await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext, "Stock.Low", new
                {
                    ItemId = itemId,
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
            _logger.LogError(ex, "Error checking low stock level for item {ItemId}", itemId);
            throw;
        }
    }
}
