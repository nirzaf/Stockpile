using Merconiq.Core.Entities;
using Merconiq.Core.Models;

namespace Merconiq.Core.Interfaces;

/// <summary>Service contract for stock movement and inventory state.</summary>
public interface IStockService
{
    /// <summary>Retrieves the current stock-in-hand for every item and location combination.</summary>
    /// <returns>A collection of stock-in-hand rows.</returns>
    Task<IEnumerable<StockInHand>> GetAllAsync();

    /// <summary>Retrieves stock only from the supplied authorized companies.</summary>
    Task<IEnumerable<StockInHand>> GetForCompaniesAsync(IReadOnlyCollection<int> companyIds);

    /// <summary>Gets the current stock-in-hand for a specific item at a specific location.</summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="locationId">The location identifier.</param>
    /// <param name="batchNumber">Optional batch or lot identifier.</param>
    /// <param name="expiryDate">Optional expiry date for the lot.</param>
    /// <returns>The stock-in-hand row, or <see langword="null"/> if none exists.</returns>
    Task<StockInHand?> GetByItemAndLocationAsync(int itemId, int locationId, string? batchNumber = null, DateTime? expiryDate = null);

    /// <summary>Gets stock transactions within an optional date range.</summary>
    /// <param name="from">Inclusive start date, or <see langword="null"/> for no lower bound.</param>
    /// <param name="to">Inclusive end date, or <see langword="null"/> for no upper bound.</param>
    /// <returns>Matching stock transactions.</returns>
    Task<IEnumerable<StockTransaction>> GetTransactionsAsync(DateTime? from, DateTime? to);

    /// <summary>Retrieves stock movements only from the supplied authorized companies.</summary>
    Task<IEnumerable<StockTransaction>> GetTransactionsForCompaniesAsync(
        DateTime? from,
        DateTime? to,
        IReadOnlyCollection<int> companyIds);

    /// <summary>Receives stock into a location, increasing on-hand quantity.</summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="locationId">The destination location identifier.</param>
    /// <param name="quantity">The quantity to receive.</param>
    /// <param name="notes">Optional free-text notes.</param>
    /// <param name="batchNumber">Optional lot/batch number.</param>
    /// <param name="expiryDate">Optional expiry date for perishable stock.</param>
    /// <param name="unitCost">Optional acquisition cost per base unit.</param>
    /// <param name="mutationScope">Company authorized by the caller before posting; revalidated under the location lock.</param>
    /// <exception cref="Exceptions.ConcurrencyException">Thrown when concurrent updates are detected after retries are exhausted.</exception>
    Task ReceiveStockAsync(
        int itemId,
        int locationId,
        int quantity,
        string? notes,
        string? batchNumber = null,
        DateTime? expiryDate = null,
        decimal? unitCost = null,
        StockMutationScope? mutationScope = null);

    /// <summary>Transfers stock between two locations atomically.</summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="fromLocationId">The source location identifier.</param>
    /// <param name="toLocationId">The destination location identifier.</param>
    /// <param name="quantity">The quantity to transfer.</param>
    /// <param name="notes">Optional free-text notes.</param>
    /// <param name="batchNumber">Optional lot/batch number.</param>
    /// <param name="expiryDate">Optional expiry date for perishable stock.</param>
    /// <param name="mutationScope">Company authorized by the caller before posting; revalidated under both location locks.</param>
    /// <param name="expiryExceptionReason">Mandatory reason plus a separate company capability when the selected lot is expired.</param>
    /// <exception cref="Exceptions.ConcurrencyException">Thrown when concurrent updates are detected after retries are exhausted.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the source location has insufficient stock.</exception>
    Task TransferStockAsync(
        int itemId,
        int fromLocationId,
        int toLocationId,
        int quantity,
        string? notes,
        string? batchNumber = null,
        DateTime? expiryDate = null,
        StockMutationScope? mutationScope = null,
        string? expiryExceptionReason = null);

    /// <summary>Sells stock out of a location, decreasing on-hand quantity.</summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="locationId">The source location identifier.</param>
    /// <param name="quantity">The quantity to sell.</param>
    /// <param name="notes">Optional free-text notes.</param>
    /// <param name="batchNumber">Optional lot/batch number.</param>
    /// <param name="expiryDate">Optional expiry date for perishable stock.</param>
    /// <param name="reservationSourceLineReference">Optional source line being consumed.</param>
    /// <param name="mutationScope">Company authorized by the caller before posting; revalidated under the location lock.</param>
    /// <param name="expiryExceptionReason">Mandatory reason plus a separate company capability when the selected lot is expired.</param>
    /// <exception cref="Exceptions.ConcurrencyException">Thrown when concurrent updates are detected after retries are exhausted.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the location has insufficient stock to sell.</exception>
    Task SellStockAsync(
        int itemId,
        int locationId,
        int quantity,
        string? notes,
        string? batchNumber = null,
        DateTime? expiryDate = null,
        string? reservationSourceLineReference = null,
        StockMutationScope? mutationScope = null,
        string? expiryExceptionReason = null);

    /// <summary>Creates a lot-specific reservation for one source document line.</summary>
    Task CreateReservationAsync(CreateStockReservationRequest request, StockMutationScope? mutationScope = null);

    /// <summary>Releases an active reservation and returns its quantity to availability.</summary>
    Task ReleaseReservationAsync(string sourceLineReference, string? reason = null, StockMutationScope? mutationScope = null);

    /// <summary>Cancels an active reservation and returns its quantity to availability.</summary>
    Task CancelReservationAsync(string sourceLineReference, string? reason = null, StockMutationScope? mutationScope = null);

    /// <summary>Consumes reserved quantity through the normal atomic sale posting.</summary>
    Task ConsumeReservationAsync(ConsumeStockReservationRequest request, StockMutationScope? mutationScope = null);

    /// <summary>Gets one reservation by its stable source-line reference.</summary>
    Task<StockReservationView?> GetReservationAsync(string sourceLineReference);

    /// <summary>Gets on-hand, reserved, and available stock by lot.</summary>
    Task<IEnumerable<StockAvailabilityView>> GetAvailabilityAsync(
        int? itemId = null,
        int? locationId = null,
        IReadOnlyCollection<int>? companyIds = null);

    /// <summary>Gets moving-average buckets with immutable valued movement evidence.</summary>
    Task<IEnumerable<StockValuationView>> GetValuationAsync(
        int? itemId = null,
        int? locationId = null,
        IReadOnlyCollection<int>? companyIds = null);
}
