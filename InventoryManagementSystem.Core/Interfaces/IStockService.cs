using InventoryManagementSystem.Core.Entities;

namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>Service contract for stock movement and inventory state.</summary>
public interface IStockService
{
    /// <summary>Retrieves the current stock-in-hand for every item and location combination.</summary>
    /// <returns>A collection of stock-in-hand rows.</returns>
    Task<IEnumerable<StockInHand>> GetAllAsync();

    /// <summary>Gets the current stock-in-hand for a specific item at a specific location.</summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="locationId">The location identifier.</param>
    /// <returns>The stock-in-hand row, or <see langword="null"/> if none exists.</returns>
    Task<StockInHand?> GetByItemAndLocationAsync(int itemId, int locationId);

    /// <summary>Gets stock transactions within an optional date range.</summary>
    /// <param name="from">Inclusive start date, or <see langword="null"/> for no lower bound.</param>
    /// <param name="to">Inclusive end date, or <see langword="null"/> for no upper bound.</param>
    /// <returns>Matching stock transactions.</returns>
    Task<IEnumerable<StockTransaction>> GetTransactionsAsync(DateTime? from, DateTime? to);

    /// <summary>Receives stock into a location, increasing on-hand quantity.</summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="locationId">The destination location identifier.</param>
    /// <param name="quantity">The quantity to receive.</param>
    /// <param name="notes">Optional free-text notes.</param>
    /// <param name="batchNumber">Optional lot/batch number.</param>
    /// <param name="expiryDate">Optional expiry date for perishable stock.</param>
    /// <exception cref="Exceptions.ConcurrencyException">Thrown when concurrent updates are detected after retries are exhausted.</exception>
    Task ReceiveStockAsync(int itemId, int locationId, int quantity, string? notes, string? batchNumber = null, DateTime? expiryDate = null);

    /// <summary>Transfers stock between two locations atomically.</summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="fromLocationId">The source location identifier.</param>
    /// <param name="toLocationId">The destination location identifier.</param>
    /// <param name="quantity">The quantity to transfer.</param>
    /// <param name="notes">Optional free-text notes.</param>
    /// <param name="batchNumber">Optional lot/batch number.</param>
    /// <param name="expiryDate">Optional expiry date for perishable stock.</param>
    /// <exception cref="Exceptions.ConcurrencyException">Thrown when concurrent updates are detected after retries are exhausted.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the source location has insufficient stock.</exception>
    Task TransferStockAsync(int itemId, int fromLocationId, int toLocationId, int quantity, string? notes, string? batchNumber = null, DateTime? expiryDate = null);

    /// <summary>Sells stock out of a location, decreasing on-hand quantity.</summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="locationId">The source location identifier.</param>
    /// <param name="quantity">The quantity to sell.</param>
    /// <param name="notes">Optional free-text notes.</param>
    /// <param name="batchNumber">Optional lot/batch number.</param>
    /// <param name="expiryDate">Optional expiry date for perishable stock.</param>
    /// <exception cref="Exceptions.ConcurrencyException">Thrown when concurrent updates are detected after retries are exhausted.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the location has insufficient stock to sell.</exception>
    Task SellStockAsync(int itemId, int locationId, int quantity, string? notes, string? batchNumber = null, DateTime? expiryDate = null);
}
