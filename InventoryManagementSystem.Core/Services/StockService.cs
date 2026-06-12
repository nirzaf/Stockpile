using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Interfaces;
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
    private readonly ILogger<StockService> _logger;

    public StockService(
        IRepository<StockInHand> stockRepo,
        IRepository<StockTransaction> txRepo,
        IRepository<Item> itemRepo,
        IUnitOfWork unitOfWork,
        IWebhookDispatcher webhookDispatcher,
        ILogger<StockService> logger)
    {
        _stockRepo = stockRepo;
        _txRepo = txRepo;
        _itemRepo = itemRepo;
        _unitOfWork = unitOfWork;
        _webhookDispatcher = webhookDispatcher;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StockInHand>> GetAllAsync() => await _stockRepo.GetAllAsync();

    /// <inheritdoc />
    public async Task<StockInHand?> GetByItemAndLocationAsync(int itemId, int locationId)
    {
        var results = await _stockRepo.FindAsync(s => s.ItemId == itemId && s.LocationId == locationId);
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

    private async Task ExecuteWithRetryAsync(Func<Task> action)
    {
        int retries = 3;
        while (true)
        {
            try
            {
                await action();
                break;
            }
            catch (InventoryManagementSystem.Core.Exceptions.ConcurrencyException ex)
            {
                if (--retries <= 0)
                {
                    _logger.LogError(ex, "Concurrency conflict could not be resolved after retries.");
                    throw;
                }
                _logger.LogWarning("Concurrency conflict detected, retrying operation. Retries remaining: {Retries}", retries);
                _unitOfWork.ClearTracker();
                await Task.Delay(100);
            }
        }
    }

    /// <inheritdoc />
    public async Task ReceiveStockAsync(int itemId, int locationId, int quantity, string? notes, string? batchNumber = null, DateTime? expiryDate = null)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive");

        await ExecuteWithRetryAsync(async () =>
        {
            var existing = await GetByItemAndLocationAsync(itemId, locationId);
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
                    Quantity = quantity
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

            await _unitOfWork.SaveChangesAsync();
        });

        _logger.LogInformation("Received {Qty} of item {ItemId} at location {LocId}", quantity, itemId, locationId);
        await _webhookDispatcher.DispatchAsync("Stock.Received", new { ItemId = itemId, LocationId = locationId, Quantity = quantity, Notes = notes, BatchNumber = batchNumber, ExpiryDate = expiryDate });
        await CheckLowStockAsync(itemId);
    }

    /// <inheritdoc />
    public async Task TransferStockAsync(int itemId, int fromLocationId, int toLocationId, int quantity, string? notes, string? batchNumber = null, DateTime? expiryDate = null)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive");
        if (fromLocationId == toLocationId) throw new ArgumentException("Source and destination must be different");

        await ExecuteWithRetryAsync(async () =>
        {
            var source = await GetByItemAndLocationAsync(itemId, fromLocationId);
            if (source == null || source.Quantity < quantity)
                throw new InvalidOperationException("Insufficient stock at source location");

            source.Quantity -= quantity;
            await _stockRepo.UpdateAsync(source);

            var dest = await GetByItemAndLocationAsync(itemId, toLocationId);
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
                    Quantity = quantity
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

            await _unitOfWork.SaveChangesAsync();
        });

        _logger.LogInformation("Transferred {Qty} of item {ItemId} from {From} to {To}", quantity, itemId, fromLocationId, toLocationId);
        await _webhookDispatcher.DispatchAsync("Stock.Transferred", new { ItemId = itemId, FromLocationId = fromLocationId, ToLocationId = toLocationId, Quantity = quantity, Notes = notes, BatchNumber = batchNumber, ExpiryDate = expiryDate });
        await CheckLowStockAsync(itemId);
    }

    /// <inheritdoc />
    public async Task SellStockAsync(int itemId, int locationId, int quantity, string? notes, string? batchNumber = null, DateTime? expiryDate = null)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive");

        await ExecuteWithRetryAsync(async () =>
        {
            var stock = await GetByItemAndLocationAsync(itemId, locationId);
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

            await _unitOfWork.SaveChangesAsync();
        });

        _logger.LogInformation("Sold {Qty} of item {ItemId} from location {LocId}", quantity, itemId, locationId);
        await _webhookDispatcher.DispatchAsync("Stock.Sold", new { ItemId = itemId, LocationId = locationId, Quantity = quantity, Notes = notes });
        await CheckLowStockAsync(itemId);
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
                await _webhookDispatcher.DispatchAsync("Stock.Low", new
                {
                    ItemId = itemId,
                    ItemCode = item.ItemCode,
                    TotalStock = totalStock,
                    ReorderLevel = item.ReorderLevel
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking low stock level for item {ItemId}", itemId);
        }
    }
}
