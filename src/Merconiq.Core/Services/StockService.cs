using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Diagnostics;
using Merconiq.Core.Exceptions;
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
    private readonly IRepository<StockReservation>? _reservationRepo;
    private readonly IRepository<StockReservationAllocation>? _reservationAllocationRepo;

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
        IRepository<StockValuationEntry> valuationEntryRepo,
        IRepository<StockReservation>? reservationRepo = null,
        IRepository<StockReservationAllocation>? reservationAllocationRepo = null)
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
        _reservationRepo = reservationRepo;
        _reservationAllocationRepo = reservationAllocationRepo;
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
    public Task<StockInHand?> GetByItemAndLocationAsync(
        int itemId,
        int locationId,
        string? batchNumber = null,
        DateTime? expiryDate = null) =>
        GetByItemAndLocationAsync(itemId, locationId, batchNumber, expiryDate, CancellationToken.None);

    /// <inheritdoc />
    public async Task<StockInHand?> GetByItemAndLocationAsync(
        int itemId,
        int locationId,
        string? batchNumber,
        DateTime? expiryDate,
        CancellationToken cancellationToken)
    {
        expiryDate = StockLotExpiryDate.Normalize(expiryDate);
        IEnumerable<StockInHand> results;
        if (expiryDate is DateTime expiryDayStart)
        {
            var expiryDayEnd = expiryDayStart.AddDays(1);
            Expression<Func<StockInHand, bool>> predicate = s =>
                s.ItemId == itemId &&
                s.LocationId == locationId &&
                s.BatchNumber == batchNumber &&
                s.ExpiryDate >= expiryDayStart &&
                s.ExpiryDate < expiryDayEnd;
            results = cancellationToken.CanBeCanceled
                ? await _stockRepo.FindAsync(predicate, cancellationToken)
                : await _stockRepo.FindAsync(predicate);
        }
        else
        {
            Expression<Func<StockInHand, bool>> predicate = s =>
                s.ItemId == itemId &&
                s.LocationId == locationId &&
                s.BatchNumber == batchNumber &&
                s.ExpiryDate == null;
            results = cancellationToken.CanBeCanceled
                ? await _stockRepo.FindAsync(predicate, cancellationToken)
                : await _stockRepo.FindAsync(predicate);
        }

        var matchingRows = results.Take(2).ToArray();
        if (matchingRows.Length > 1)
            throw new StockAvailabilityConflictException(
                "Multiple stock rows match the same lot expiry date; reconcile inventory before changing it.");

        return matchingRows.FirstOrDefault();
    }

    private async Task<StockInHand?> GetStockForRequestedLotAsync(
        int itemId,
        int locationId,
        string? batchNumber,
        DateTime? expiryDate,
        CancellationToken cancellationToken = default)
    {
        if (batchNumber is null || expiryDate.HasValue)
            return cancellationToken.CanBeCanceled
                ? await GetByItemAndLocationAsync(itemId, locationId, batchNumber, expiryDate, cancellationToken)
                : await GetByItemAndLocationAsync(itemId, locationId, batchNumber, expiryDate);

        Expression<Func<StockInHand, bool>> predicate = stock =>
            stock.ItemId == itemId && stock.LocationId == locationId && stock.BatchNumber == batchNumber;
        var rows = cancellationToken.CanBeCanceled
            ? await _stockRepo.FindAsync(predicate, cancellationToken)
            : await _stockRepo.FindAsync(predicate);
        var matches = rows
            .Take(2)
            .ToArray();
        if (matches.Length > 1)
            throw new StockAvailabilityConflictException(
                "Multiple stock rows match the same batch number; provide an expiry date to select one lot.");
        return matches.FirstOrDefault();
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

    /// <inheritdoc />
    public async Task<StockTransaction?> GetTransactionAsync(int transactionId)
    {
        if (transactionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(transactionId));

        return (await _txRepo.FindAsync(transaction => transaction.Id == transactionId)).FirstOrDefault();
    }

    private async Task ExecuteWithRetryAsync(
        int itemId,
        Func<Task> action,
        Func<Task<bool>> verifySucceeded,
        bool checkLowStock = true,
        CancellationToken cancellationToken = default)
    {
        // PostgreSQL surfaces an optimistic-concurrency conflict as a DbUpdateConcurrencyException
        // (driven by the StockInHand.xmin token). Three retries matches the default
        // EnableRetryOnFailure(3) budget from Program.cs so callers get a single, coherent
        // retry envelope across the system.
        int retries = 3;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ownsTransaction = !_unitOfWork.HasActiveTransaction;
            try
            {
                await _unitOfWork.ExecuteInTransactionAsync(async () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Keep stock postings in the same lock domain as company currency
                    // freeze checks. ponytail: tenant-wide serialization is the smallest
                    // correct boundary; split by company if throughput requires it.
                    await _unitOfWork.AcquireTenantOperationLockAsync("organization-state", cancellationToken);
                    var item = cancellationToken.CanBeCanceled
                        ? await _itemRepo.GetByIdAsync(itemId, cancellationToken)
                        : await _itemRepo.GetByIdAsync(itemId);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item is not null && !item.IsActive)
                        throw new InvalidOperationException("Inactive items cannot be used in stock operations.");
                    await action();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item is not null && checkLowStock)
                        await CheckLowStockAsync(item);
                }, cancellationToken, verifySucceeded);
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
        decimal? unitCost = null,
        StockMutationScope? mutationScope = null)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive");
        if (unitCost is < 0) throw new ArgumentException("Unit cost must be non-negative");
        expiryDate = StockLotExpiryDate.Normalize(expiryDate);
        if (unitCost.HasValue && (batchNumber is not null || expiryDate.HasValue))
            throw new InvalidOperationException("Valuation is scoped to unbatched stock.");

        StockTransaction? transaction = null;
        await ExecuteWithRetryAsync(itemId, async () =>
        {
            await _unitOfWork.AcquireLocationLocksAsync([locationId]);
            var location = await EnsureLocationUsableAsync(locationId);
            await EnsureAuthorizedCompanyScopeAsync(location, mutationScope);
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
    public async Task TransferStockAsync(
        int itemId,
        int fromLocationId,
        int toLocationId,
        int quantity,
        string? notes,
        string? batchNumber = null,
        DateTime? expiryDate = null,
        StockMutationScope? mutationScope = null,
        string? expiryExceptionReason = null)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive");
        if (fromLocationId == toLocationId) throw new ArgumentException("Source and destination must be different");
        expiryDate = StockLotExpiryDate.Normalize(expiryDate);
        EnsureExpiryExceptionReasonLength(expiryExceptionReason);

        StockTransaction? transaction = null;
        await ExecuteWithRetryAsync(itemId, async () =>
        {
            await _unitOfWork.AcquireLocationLocksAsync([fromLocationId, toLocationId]);
            var sourceLocation = await EnsureLocationUsableAsync(fromLocationId);
            var destinationLocation = await EnsureLocationUsableAsync(toLocationId);
            await EnsureAuthorizedCompanyScopeAsync(sourceLocation, mutationScope);
            await EnsureAuthorizedCompanyScopeAsync(destinationLocation, mutationScope);
            await EnsureSameCompanyTransferAsync(sourceLocation, destinationLocation);
            var initiallySelectedSource = await GetStockForRequestedLotAsync(
                itemId, fromLocationId, batchNumber, expiryDate);
            if (initiallySelectedSource is null)
                throw new InvalidOperationException("Insufficient stock at source location");
            await EnsureExpiredLotExceptionAsync(
                initiallySelectedSource.ExpiryDate, expiryExceptionReason, mutationScope);
            await ReleaseExpiredReservationsAsync(itemId, fromLocationId);
            await ReleaseExpiredReservationsAsync(itemId, toLocationId);
            var source = await GetStockForRequestedLotAsync(itemId, fromLocationId, batchNumber, expiryDate);
            if (source == null)
                throw new InvalidOperationException("Insufficient stock at source location");
            var normalizedExpiryExceptionReason = await EnsureExpiredLotExceptionAsync(
                source.ExpiryDate, expiryExceptionReason, mutationScope);
            if (source.Quantity < quantity)
                throw new InvalidOperationException("Insufficient stock at source location");
            EnsureAvailable(source, quantity, "transfer");

            source.Quantity -= quantity;
            await _stockRepo.UpdateAsync(source);

            var dest = await GetByItemAndLocationAsync(
                itemId, toLocationId, source.BatchNumber, source.ExpiryDate);
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
                    BatchNumber = source.BatchNumber,
                    ExpiryDate = source.ExpiryDate
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
                BatchNumber = source.BatchNumber,
                ExpiryDate = source.ExpiryDate,
                Notes = notes,
                ExpiryExceptionReason = normalizedExpiryExceptionReason
            };
            await _txRepo.AddAsync(transaction);

            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext, "Stock.Transferred",
                new { ItemId = itemId, FromLocationId = fromLocationId, ToLocationId = toLocationId, Quantity = quantity, Notes = notes, BatchNumber = source.BatchNumber, ExpiryDate = source.ExpiryDate, ExpiryExceptionReason = normalizedExpiryExceptionReason }));
            await _unitOfWork.SaveChangesAsync();
        }, () => VerifyTransactionCommitAsync(transaction));

        _logger.LogInformation("Transferred {Qty} of item {ItemId} from {From} to {To}", quantity, itemId, fromLocationId, toLocationId);
    }

    /// <inheritdoc />
    public Task SellStockAsync(
        int itemId,
        int locationId,
        int quantity,
        string? notes,
        string? batchNumber = null,
        DateTime? expiryDate = null,
        string? reservationSourceLineReference = null,
        StockMutationScope? mutationScope = null,
        string? expiryExceptionReason = null) =>
        SellStockCoreAsync(
            itemId,
            locationId,
            quantity,
            notes,
            batchNumber,
            expiryDate,
            reservationSourceLineReference,
            mutationScope,
            expiryExceptionReason,
            checkLowStock: true,
            movementSourceLineReference: null);

    private async Task SellStockCoreAsync(
        int itemId,
        int locationId,
        int quantity,
        string? notes,
        string? batchNumber,
        DateTime? expiryDate,
        string? reservationSourceLineReference,
        StockMutationScope? mutationScope,
        string? expiryExceptionReason,
        bool checkLowStock,
        string? movementSourceLineReference)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive");
        expiryDate = StockLotExpiryDate.Normalize(expiryDate);
        EnsureExpiryExceptionReasonLength(expiryExceptionReason);

        StockTransaction? transaction = null;
        await ExecuteWithRetryAsync(itemId, async () =>
        {
            await _unitOfWork.AcquireLocationLocksAsync([locationId]);
            var location = await EnsureLocationUsableAsync(locationId);
            await EnsureAuthorizedCompanyScopeAsync(location, mutationScope);
            var initiallySelectedStock = await GetStockForRequestedLotAsync(
                itemId, locationId, batchNumber, expiryDate);
            if (initiallySelectedStock is null)
                throw new InvalidOperationException("Insufficient stock for sale");
            var normalizedExpiryExceptionReason = await EnsureExpiredLotExceptionAsync(
                initiallySelectedStock.ExpiryDate, expiryExceptionReason, mutationScope);
            await ReleaseExpiredReservationsAsync(itemId, locationId);
            var stock = await GetStockForRequestedLotAsync(itemId, locationId, batchNumber, expiryDate);
            if (stock is null)
                throw new InvalidOperationException("Insufficient stock for sale");
            normalizedExpiryExceptionReason = await EnsureExpiredLotExceptionAsync(
                stock.ExpiryDate, expiryExceptionReason, mutationScope);
            if (stock.Quantity < quantity)
                throw new InvalidOperationException("Insufficient stock for sale");

            StockReservation? reservation = null;
            var reservationRemaining = 0;
            if (!string.IsNullOrWhiteSpace(reservationSourceLineReference))
            {
                EnsureReservationRepository();
                reservation = await FindReservationAsync(reservationSourceLineReference.Trim());
                if (reservation is null || reservation.Status != StockReservationStatus.Active)
                    throw new StockAvailabilityConflictException("Reservation is not active.");
                if (reservation.ExpiresAt <= DateTimeOffset.UtcNow)
                    throw new StockAvailabilityConflictException("Reservation has expired.");
                if (reservation.ItemId != itemId || reservation.LocationId != locationId)
                    throw new StockAvailabilityConflictException("Reservation does not match the requested stock lot.");

                var loadedAllocations = await LoadReservationAllocationsAsync(reservation);
                if (loadedAllocations.Allocations.Sum(Remaining) != Remaining(reservation))
                    throw new StockAvailabilityConflictException("Stock reservation allocations are inconsistent.");
                var allocation = loadedAllocations.Allocations.SingleOrDefault(candidate =>
                    candidate.BatchNumber == stock.BatchNumber &&
                    StockLotExpiryDate.Normalize(candidate.ExpiryDate) == StockLotExpiryDate.Normalize(stock.ExpiryDate));
                if (allocation is null)
                    throw new StockAvailabilityConflictException("Reservation does not include the requested stock lot.");

                reservationRemaining = Remaining(allocation);
                if (reservationRemaining < quantity)
                    throw new StockAvailabilityConflictException("The reservation does not contain enough remaining quantity.");

                var reservedForOtherLines = stock.ReservedQuantity - reservationRemaining;
                if (reservedForOtherLines < 0)
                    throw new StockAvailabilityConflictException("Stock reservation counters are inconsistent.");
                EnsureAvailable(stock, quantity, "reservation consumption", reservedForOtherLines);
                stock.ReservedQuantity -= quantity;
                reservation.ConsumedQuantity = checked(reservation.ConsumedQuantity + quantity);
                allocation.ConsumedQuantity = checked(allocation.ConsumedQuantity + quantity);
                if (Remaining(reservation) == 0)
                {
                    reservation.Status = StockReservationStatus.Consumed;
                    reservation.ClosedAt = DateTimeOffset.UtcNow;
                    reservation.ResolutionReason = "Consumed";
                }
                var trackedReservation = await _reservationRepo!.GetByIdAsync(reservation.Id)
                    ?? throw new StockAvailabilityConflictException("Reservation no longer exists.");
                trackedReservation.ConsumedQuantity = reservation.ConsumedQuantity;
                trackedReservation.Status = reservation.Status;
                trackedReservation.ClosedAt = reservation.ClosedAt;
                trackedReservation.ResolutionReason = reservation.ResolutionReason;
                if (loadedAllocations.Persisted)
                {
                    var trackedAllocation = await _reservationAllocationRepo!.GetByIdAsync(allocation.Id)
                        ?? throw new StockAvailabilityConflictException("Reservation allocation no longer exists.");
                    trackedAllocation.ConsumedQuantity = allocation.ConsumedQuantity;
                }
            }
            else
            {
                EnsureAvailable(stock, quantity, "sale");
            }

            stock.Quantity -= quantity;
            await _stockRepo.UpdateAsync(stock);

            transaction = new StockTransaction
            {
                ItemId = itemId,
                FromLocationId = locationId,
                Quantity = quantity,
                TransactionType = TransactionType.Sell,
                SourceLineReference = movementSourceLineReference,
                TransactionDate = DateTime.UtcNow,
                BatchNumber = stock.BatchNumber,
                ExpiryDate = stock.ExpiryDate,
                Notes = notes,
                ExpiryExceptionReason = normalizedExpiryExceptionReason
            };
            await _txRepo.AddAsync(transaction);

            if (stock.BatchNumber is null && stock.ExpiryDate is null)
            {
                await ApplySaleValuationAsync(itemId, locationId, quantity, transaction);
            }

            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext, "Stock.Sold",
                new
                {
                    ItemId = itemId,
                    LocationId = locationId,
                    Quantity = quantity,
                    Notes = notes,
                    BatchNumber = stock.BatchNumber,
                    ExpiryDate = stock.ExpiryDate,
                    ExpiryExceptionReason = normalizedExpiryExceptionReason,
                    ReservationSourceLineReference = reservationSourceLineReference?.Trim(),
                    MovementSourceLineReference = transaction.SourceLineReference
                }));
            await _unitOfWork.SaveChangesAsync();
        }, () => VerifyTransactionCommitAsync(transaction), checkLowStock: checkLowStock);

        _logger.LogInformation("Sold {Qty} of item {ItemId} from location {LocId}", quantity, itemId, locationId);
    }

    /// <inheritdoc />
    public async Task ReturnStockAsync(
        CreateStockReturnRequest request,
        StockMutationScope? mutationScope = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OriginalTransactionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.OriginalTransactionId));
        if (request.Quantity <= 0)
            throw new ArgumentException("Return quantity must be positive.", nameof(request));
        if (!Enum.IsDefined(request.Disposition))
            throw new ArgumentException("Return disposition is invalid.", nameof(request));

        EnsureSourceLineReference(request.SourceLineReference);
        EnsureReservationFields(null, request.Notes);
        var sourceLineReference = request.SourceLineReference.Trim();
        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        var initialOriginal = await GetTransactionAsync(request.OriginalTransactionId)
            ?? throw new KeyNotFoundException("Original stock transaction not found.");
        if (initialOriginal.TransactionType != TransactionType.Sell)
            throw new InvalidOperationException("Only sale transactions can be returned.");

        StockTransaction? returnTransaction = null;
        await ExecuteWithRetryAsync(initialOriginal.ItemId, async () =>
        {
            await _unitOfWork.AcquireLocationLocksAsync([initialOriginal.FromLocationId]);
            var original = await GetTransactionAsync(request.OriginalTransactionId)
                ?? throw new KeyNotFoundException("Original stock transaction not found.");
            if (original.TransactionType != TransactionType.Sell)
                throw new InvalidOperationException("Only sale transactions can be returned.");

            var location = await EnsureLocationUsableAsync(original.FromLocationId);
            await EnsureAuthorizedCompanyScopeAsync(location, mutationScope);

            var existingReturn = (await _txRepo.FindAsync(transaction =>
                transaction.TransactionType == TransactionType.Return &&
                transaction.SourceLineReference == sourceLineReference)).FirstOrDefault();
            if (existingReturn is not null)
            {
                if (existingReturn.OriginalTransactionId == original.Id &&
                    existingReturn.Quantity == request.Quantity &&
                    existingReturn.ReturnDisposition == request.Disposition &&
                    existingReturn.Notes == notes)
                {
                    returnTransaction = existingReturn;
                    return;
                }

                throw new InvalidOperationException("The source line already has a different return.");
            }

            var priorReturns = await _txRepo.FindAsync(transaction =>
                transaction.TransactionType == TransactionType.Return &&
                transaction.OriginalTransactionId == original.Id);
            var eligibleQuantity = original.Quantity - priorReturns.Sum(transaction => transaction.Quantity);
            if (request.Quantity > eligibleQuantity)
                throw new StockAvailabilityConflictException(
                    $"Return quantity exceeds the eligible quantity of {Math.Max(0, eligibleQuantity)}.");

            if (request.Disposition == StockReturnDisposition.Restockable)
                EnsureLotNotExpired(original.ExpiryDate);

            var stock = await GetByItemAndLocationAsync(
                original.ItemId, original.FromLocationId, original.BatchNumber, original.ExpiryDate);
            if (stock is null)
            {
                stock = new StockInHand
                {
                    ItemId = original.ItemId,
                    LocationId = original.FromLocationId,
                    Quantity = 0,
                    BatchNumber = original.BatchNumber,
                    ExpiryDate = original.ExpiryDate
                };
                await _stockRepo.AddAsync(stock);
            }

            stock.Quantity = checked(stock.Quantity + request.Quantity);
            if (request.Disposition != StockReturnDisposition.Restockable)
                stock.QuarantinedQuantity = checked(stock.QuarantinedQuantity + request.Quantity);
            await _stockRepo.UpdateAsync(stock);

            var originalValuation = (await _valuationEntryRepo.FindAsync(entry =>
                entry.StockTransactionId == original.Id && entry.EntryType == StockValuationEntryType.Sale))
                .SingleOrDefault();
            var unitCost = originalValuation?.UnitCost;
            returnTransaction = new StockTransaction
            {
                ItemId = original.ItemId,
                FromLocationId = original.FromLocationId,
                ToLocationId = original.FromLocationId,
                Quantity = request.Quantity,
                TransactionType = TransactionType.Return,
                TransactionDate = DateTime.UtcNow,
                BatchNumber = original.BatchNumber,
                ExpiryDate = original.ExpiryDate,
                Notes = notes,
                OriginalTransactionId = original.Id,
                SourceLineReference = sourceLineReference,
                ReturnDisposition = request.Disposition,
                UnitCost = unitCost
            };
            await _txRepo.AddAsync(returnTransaction);

            if (unitCost is decimal returnedUnitCost)
                await ApplyReturnValuationAsync(
                    original.ItemId, original.FromLocationId, request.Quantity, returnedUnitCost, returnTransaction);

            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext, "Stock.Returned",
                new
                {
                    OriginalTransactionId = original.Id,
                    original.ItemId,
                    LocationId = original.FromLocationId,
                    request.Quantity,
                    request.Disposition,
                    SourceLineReference = sourceLineReference,
                    UnitCost = unitCost,
                    Notes = notes,
                    original.BatchNumber,
                    original.ExpiryDate
                }));
            await _unitOfWork.SaveChangesAsync();
        }, () => VerifyTransactionCommitAsync(returnTransaction));

        _logger.LogInformation("Returned {Qty} of item {ItemId} against sale {TransactionId}",
            request.Quantity, initialOriginal.ItemId, initialOriginal.Id);
    }

    /// <inheritdoc />
    public Task QuarantineStockAsync(
        ChangeStockQuarantineRequest request,
        StockMutationScope? mutationScope = null,
        CancellationToken cancellationToken = default) =>
        ChangeStockQuarantineAsync(request, TransactionType.Quarantine, mutationScope, cancellationToken);

    /// <inheritdoc />
    public Task ReleaseQuarantinedStockAsync(
        ChangeStockQuarantineRequest request,
        StockMutationScope? mutationScope = null,
        CancellationToken cancellationToken = default) =>
        ChangeStockQuarantineAsync(request, TransactionType.QuarantineRelease, mutationScope, cancellationToken);

    private async Task ChangeStockQuarantineAsync(
        ChangeStockQuarantineRequest request,
        TransactionType transactionType,
        StockMutationScope? mutationScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ItemId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.ItemId));
        if (request.LocationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.LocationId));
        if (request.Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(request.Quantity), "Quantity must be positive.");

        EnsureSourceLineReference(request.SourceLineReference);
        EnsureReservationFields(request.BatchNumber, request.Reason);
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new ArgumentException("A quarantine reason is required.", nameof(request));

        var sourceLineReference = request.SourceLineReference.Trim();
        var reason = request.Reason.Trim();
        var expiryDate = StockLotExpiryDate.Normalize(request.ExpiryDate);
        StockTransaction? transaction = null;

        await ExecuteWithRetryAsync(request.ItemId, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _unitOfWork.AcquireLocationLocksAsync([request.LocationId], cancellationToken);
            var location = await EnsureLocationUsableAsync(request.LocationId, cancellationToken);
            await EnsureAuthorizedCompanyScopeAsync(location, mutationScope);
            cancellationToken.ThrowIfCancellationRequested();

            if (transactionType == TransactionType.QuarantineRelease &&
                (mutationScope?.ReauthorizeQuarantinedStockOverride is not { } reauthorizeOverride ||
                 !await reauthorizeOverride()))
            {
                throw new UnauthorizedAccessException(
                    "An explicit company-scoped quarantined-stock override capability is required.");
            }

            var priorTransactions = cancellationToken.CanBeCanceled
                ? await _txRepo.FindAsync(candidate =>
                    candidate.SourceLineReference == sourceLineReference, cancellationToken)
                : await _txRepo.FindAsync(candidate =>
                    candidate.SourceLineReference == sourceLineReference);
            var priorTransaction = priorTransactions.FirstOrDefault();
            cancellationToken.ThrowIfCancellationRequested();
            if (priorTransaction is not null)
            {
                var sameLot = priorTransaction.BatchNumber == request.BatchNumber &&
                    StockLotExpiryDate.Normalize(priorTransaction.ExpiryDate) == expiryDate;
                if (priorTransaction.TransactionType == transactionType &&
                    priorTransaction.ItemId == request.ItemId &&
                    priorTransaction.FromLocationId == request.LocationId &&
                    priorTransaction.Quantity == request.Quantity &&
                    priorTransaction.QuarantineReason == reason &&
                    sameLot)
                {
                    transaction = priorTransaction;
                    return;
                }

                throw new StockAvailabilityConflictException(
                    "The source line already identifies a different stock quarantine operation.");
            }

            var stock = await GetStockForRequestedLotAsync(
                request.ItemId, request.LocationId, request.BatchNumber, expiryDate, cancellationToken)
                ?? throw new StockAvailabilityConflictException(
                    "No stock row matches the requested quarantine lot.");
            cancellationToken.ThrowIfCancellationRequested();
            if (!expiryDate.HasValue && stock.ExpiryDate.HasValue)
            {
                throw new StockAvailabilityConflictException(
                    "Provide the expiry date to identify this dated stock lot.");
            }

            if (transactionType == TransactionType.Quarantine)
            {
                EnsureAvailable(stock, request.Quantity, "quarantine");
                stock.QuarantinedQuantity = checked(stock.QuarantinedQuantity + request.Quantity);
            }
            else
            {
                if (stock.QuarantinedQuantity < request.Quantity)
                    throw new StockAvailabilityConflictException(
                        "Insufficient quarantined stock is available for release.");

                stock.QuarantinedQuantity -= request.Quantity;
            }

            await _stockRepo.UpdateAsync(stock);
            cancellationToken.ThrowIfCancellationRequested();
            transaction = new StockTransaction
            {
                ItemId = request.ItemId,
                FromLocationId = request.LocationId,
                Quantity = request.Quantity,
                TransactionType = transactionType,
                TransactionDate = DateTime.UtcNow,
                BatchNumber = stock.BatchNumber,
                ExpiryDate = stock.ExpiryDate,
                SourceLineReference = sourceLineReference,
                QuarantineReason = reason
            };
            await _txRepo.AddAsync(transaction);

            var eventType = transactionType == TransactionType.Quarantine
                ? "Stock.Quarantined"
                : "Stock.QuarantineReleased";
            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(
                _tenantContext,
                eventType,
                new StockQuarantineWebhookPayload(
                    request.ItemId,
                    request.LocationId,
                    request.Quantity,
                    sourceLineReference,
                    stock.BatchNumber,
                    stock.ExpiryDate,
                    reason)));
            cancellationToken.ThrowIfCancellationRequested();
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        },
            () => VerifyTransactionCommitAsync(transaction),
            checkLowStock: false,
            cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task CreateReservationAsync(
        CreateStockReservationRequest request,
        StockMutationScope? mutationScope = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with { ExpiryDate = StockLotExpiryDate.Normalize(request.ExpiryDate) };
        EnsureReservationRequest(request.Quantity, request.SourceLineReference);
        EnsureReservationFields(request.BatchNumber, request.ExpiryExceptionReason);
        EnsureReservationRepository();

        var sourceLineReference = request.SourceLineReference.Trim();
        var requestedExpiryExceptionReason = string.IsNullOrWhiteSpace(request.ExpiryExceptionReason)
            ? null
            : request.ExpiryExceptionReason.Trim();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = request.ExpiresAt ?? now.AddHours(24);
        if (expiresAt <= now)
            throw new ArgumentException("Reservation expiry must be in the future.", nameof(request));

        await ExecuteWithRetryAsync(request.ItemId, async () =>
        {
            await _unitOfWork.AcquireLocationLocksAsync([request.LocationId]);
            var location = await EnsureLocationUsableAsync(request.LocationId);
            await EnsureAuthorizedCompanyScopeAsync(location, mutationScope);
            var existing = await FindReservationAsync(sourceLineReference);
            if (existing is not null)
            {
                if (existing.Status == StockReservationStatus.Active &&
                    existing.ExpiresAt > now &&
                    existing.ItemId == request.ItemId && existing.LocationId == request.LocationId &&
                    existing.Quantity == request.Quantity &&
                    (request.ExpiresAt is null || existing.ExpiresAt == expiresAt) &&
                    (request.BatchNumber is null && !request.ExpiryDate.HasValue ||
                     existing.BatchNumber == request.BatchNumber && existing.ExpiryDate == request.ExpiryDate) &&
                    await ReservationReplayReasonMatchesAsync(existing, requestedExpiryExceptionReason))
                    return;

                if (existing.Status == StockReservationStatus.Active && existing.ExpiresAt <= now)
                    await ReleaseExpiredReservationsAsync(request.ItemId, request.LocationId);

                throw new InvalidOperationException("The source line already has a different or closed reservation.");
            }

            var initiallySelectedLots = await SelectStockLotsForReservationAsync(
                request.ItemId,
                request.LocationId,
                request.BatchNumber,
                request.ExpiryDate,
                request.Quantity,
                requestedExpiryExceptionReason is not null);
            foreach (var initialExpiredLot in initiallySelectedLots.Where(lot => IsExpiredStockLot(lot.Stock.ExpiryDate)))
            {
                await EnsureExpiredLotExceptionAsync(
                    initialExpiredLot.Stock.ExpiryDate, requestedExpiryExceptionReason, mutationScope);
            }
            if (requestedExpiryExceptionReason is null &&
                request.BatchNumber is null && !request.ExpiryDate.HasValue &&
                initiallySelectedLots.Sum(lot => lot.Quantity) < request.Quantity &&
                !await HasEnoughUnexpiredOnHandAsync(request.ItemId, request.LocationId, request.Quantity))
            {
                throw new StockAvailabilityConflictException("No stock is available for the requested lot.");
            }
            await ReleaseExpiredReservationsAsync(request.ItemId, request.LocationId);

            var selectedLots = await SelectStockLotsForReservationAsync(
                request.ItemId,
                request.LocationId,
                request.BatchNumber,
                request.ExpiryDate,
                request.Quantity,
                requestedExpiryExceptionReason is not null);
            if (selectedLots.Sum(lot => lot.Quantity) != request.Quantity)
                throw new StockAvailabilityConflictException("No stock is available for the requested lot.");
            foreach (var selectedLot in selectedLots)
                EnsureAvailable(selectedLot.Stock, selectedLot.Quantity, "reservation");

            var expiredLots = selectedLots
                .Where(lot => IsExpiredStockLot(lot.Stock.ExpiryDate))
                .ToArray();
            foreach (var expiredLot in expiredLots)
            {
                await EnsureExpiredLotExceptionAsync(
                    expiredLot.Stock.ExpiryDate, requestedExpiryExceptionReason, mutationScope);
            }
            if (requestedExpiryExceptionReason is not null && expiredLots.Length == 0)
            {
                await EnsureExpiredLotExceptionAsync(
                    selectedLots[0].Stock.ExpiryDate, requestedExpiryExceptionReason, mutationScope);
            }
            if (_reservationAllocationRepo is null && selectedLots.Count > 1)
                throw new InvalidOperationException("Multi-lot reservation persistence is not configured.");

            var reservation = new StockReservation
            {
                ItemId = request.ItemId,
                LocationId = request.LocationId,
                SourceLineReference = sourceLineReference,
                BatchNumber = selectedLots.Count == 1 ? selectedLots[0].Stock.BatchNumber : null,
                ExpiryDate = selectedLots.Count == 1 ? selectedLots[0].Stock.ExpiryDate : null,
                Quantity = request.Quantity,
                ExpiresAt = expiresAt,
                ExpiryExceptionReason = selectedLots.Count == 1 && expiredLots.Length == 1
                    ? requestedExpiryExceptionReason
                    : null
            };
            await (_reservationRepo ?? throw new InvalidOperationException(
                "Stock reservation persistence is not configured.")).AddAsync(reservation);
            var allocations = new List<StockReservationAllocation>(selectedLots.Count);
            for (var index = 0; index < selectedLots.Count; index++)
            {
                var selectedLot = selectedLots[index];
                selectedLot.Stock.ReservedQuantity = checked(selectedLot.Stock.ReservedQuantity + selectedLot.Quantity);
                await _stockRepo.UpdateAsync(selectedLot.Stock);
                allocations.Add(new StockReservationAllocation
                {
                    Reservation = reservation,
                    Ordinal = index,
                    BatchNumber = selectedLot.Stock.BatchNumber,
                    ExpiryDate = selectedLot.Stock.ExpiryDate,
                    Quantity = selectedLot.Quantity,
                    ExpiryExceptionReason = IsExpiredStockLot(selectedLot.Stock.ExpiryDate)
                        ? requestedExpiryExceptionReason
                        : null
                });
            }
            reservation.Allocations = allocations;
            if (_reservationAllocationRepo is not null)
            {
                foreach (var allocation in allocations)
                    await _reservationAllocationRepo.AddAsync(allocation);
            }

            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext, "Stock.Reserved",
                new
                {
                    reservation.ItemId,
                    reservation.LocationId,
                    reservation.SourceLineReference,
                    reservation.Quantity,
                    reservation.BatchNumber,
                    reservation.ExpiryDate,
                    reservation.ExpiryExceptionReason,
                    reservation.ExpiresAt,
                    Allocations = allocations.Select(allocation => new
                    {
                        allocation.BatchNumber,
                        allocation.ExpiryDate,
                        allocation.Quantity,
                        allocation.ExpiryExceptionReason
                    }).ToArray()
                }));
            await _unitOfWork.SaveChangesAsync();
        }, () => Task.FromResult(true));
    }

    /// <inheritdoc />
    public Task ReleaseReservationAsync(
        string sourceLineReference,
        string? reason = null,
        StockMutationScope? mutationScope = null) =>
        ChangeReservationStateAsync(sourceLineReference, StockReservationStatus.Released, reason, mutationScope);

    /// <inheritdoc />
    public Task CancelReservationAsync(
        string sourceLineReference,
        string? reason = null,
        StockMutationScope? mutationScope = null) =>
        ChangeReservationStateAsync(sourceLineReference, StockReservationStatus.Cancelled, reason, mutationScope);

    /// <inheritdoc />
    public async Task ConsumeReservationAsync(
        ConsumeStockReservationRequest request,
        StockMutationScope? mutationScope = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureReservationRequest(request.Quantity, request.SourceLineReference);
        EnsureReservationFields(null, request.Notes);
        EnsureReservationRepository();
        var sourceLineReference = request.SourceLineReference.Trim();
        var initialReservation = await FindReservationAsync(sourceLineReference)
            ?? throw new KeyNotFoundException("Reservation not found.");
        if (initialReservation.Status != StockReservationStatus.Active)
            throw new StockAvailabilityConflictException("Reservation is not active.");
        EnsureExpiryExceptionReasonLength(request.ExpiryExceptionReason);
        var initialAllocations = await LoadReservationAllocationsAsync(initialReservation);
        var initiallyExpiredAllocations = initialAllocations.Allocations
            .Where(allocation => Remaining(allocation) > 0 && IsExpiredStockLot(allocation.ExpiryDate))
            .ToArray();
        foreach (var allocation in initiallyExpiredAllocations)
        {
            await EnsureExpiredLotExceptionAsync(
                allocation.ExpiryDate, request.ExpiryExceptionReason, mutationScope);
        }
        if (initiallyExpiredAllocations.Length == 0 && !string.IsNullOrWhiteSpace(request.ExpiryExceptionReason))
        {
            await EnsureExpiredLotExceptionAsync(
                initialAllocations.Allocations[0].ExpiryDate, request.ExpiryExceptionReason, mutationScope);
        }
        if (initialReservation.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await ReleaseReservationAsync(sourceLineReference, "Expired", mutationScope);
            throw new StockAvailabilityConflictException("Reservation has expired.");
        }
        var expectedConsumedQuantity = 0;

        await ExecuteWithRetryAsync(initialReservation.ItemId, async () =>
        {
            await _unitOfWork.AcquireLocationLocksAsync([initialReservation.LocationId]);
            var location = await EnsureLocationUsableAsync(initialReservation.LocationId);
            await EnsureAuthorizedCompanyScopeAsync(location, mutationScope);
            var reservation = await FindReservationAsync(sourceLineReference)
                ?? throw new KeyNotFoundException("Reservation not found.");
            if (reservation.Status != StockReservationStatus.Active)
                throw new StockAvailabilityConflictException("Reservation is not active.");
            if (reservation.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new StockAvailabilityConflictException("Reservation has expired.");

            var loaded = await LoadReservationAllocationsAsync(reservation);
            if (loaded.Allocations.Sum(Remaining) != Remaining(reservation))
                throw new StockAvailabilityConflictException("Stock reservation allocations are inconsistent.");
            if (Remaining(reservation) < request.Quantity)
                throw new StockAvailabilityConflictException("The reservation does not contain enough remaining quantity.");
            expectedConsumedQuantity = checked(reservation.ConsumedQuantity + request.Quantity);

            var hasExpiredAllocation = loaded.Allocations.Any(allocation =>
                Remaining(allocation) > 0 && IsExpiredStockLot(allocation.ExpiryDate));
            if (!hasExpiredAllocation && !string.IsNullOrWhiteSpace(request.ExpiryExceptionReason))
            {
                await EnsureExpiredLotExceptionAsync(
                    loaded.Allocations[0].ExpiryDate, request.ExpiryExceptionReason, mutationScope);
            }

            var quantityLeft = request.Quantity;
            foreach (var allocation in loaded.Allocations.OrderBy(allocation => allocation.Ordinal))
            {
                if (quantityLeft == 0)
                    break;
                var allocationRemaining = Remaining(allocation);
                if (allocationRemaining <= 0)
                    continue;
                var quantityFromLot = Math.Min(quantityLeft, allocationRemaining);
                var expired = IsExpiredStockLot(allocation.ExpiryDate);
                await SellStockCoreAsync(
                    reservation.ItemId,
                    reservation.LocationId,
                    quantityFromLot,
                    request.Notes,
                    allocation.BatchNumber,
                    allocation.ExpiryDate,
                    sourceLineReference,
                    mutationScope,
                    expired ? request.ExpiryExceptionReason : null,
                    checkLowStock: false,
                    movementSourceLineReference: CreateReservationConsumptionReference(
                        sourceLineReference, reservation.ConsumedQuantity, allocation.Ordinal));
                quantityLeft -= quantityFromLot;
            }

            if (quantityLeft != 0)
                throw new StockAvailabilityConflictException("The reservation does not contain enough allocated stock.");
        },
            async () => expectedConsumedQuantity > 0 &&
                        await VerifyReservationConsumptionAsync(sourceLineReference, expectedConsumedQuantity));
    }

    /// <inheritdoc />
    public async Task<StockReservationView?> GetReservationAsync(string sourceLineReference)
    {
        EnsureReservationRepository();
        if (string.IsNullOrWhiteSpace(sourceLineReference))
            throw new ArgumentException("Source line reference is required.", nameof(sourceLineReference));
        var reservation = await FindReservationAsync(sourceLineReference.Trim());
        if (reservation is null)
            return null;
        var allocations = await LoadReservationAllocationsAsync(reservation);
        return ToView(reservation, allocations.Allocations);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StockAvailabilityView>> GetAvailabilityAsync(
        int? itemId = null,
        int? locationId = null,
        IReadOnlyCollection<int>? companyIds = null)
    {
        EnsureReservationRepository();
        if (companyIds is { Count: 0 })
            return [];

        var now = DateTimeOffset.UtcNow;
        var stock = await _stockRepo.FindAsync(row =>
            (!itemId.HasValue || row.ItemId == itemId.Value) &&
            (!locationId.HasValue || row.LocationId == locationId.Value) &&
            (companyIds == null || (row.Location.Branch != null && companyIds.Contains(row.Location.Branch.CompanyId))));
        var reservations = (await _reservationRepo!.FindAsync(row =>
            row.Status == StockReservationStatus.Active && row.ExpiresAt > now &&
            (!itemId.HasValue || row.ItemId == itemId.Value) &&
            (!locationId.HasValue || row.LocationId == locationId.Value))).ToArray();
        var allocationByReservation = new Dictionary<int, IReadOnlyList<StockReservationAllocation>>();
        if (_reservationAllocationRepo is not null && reservations.Length > 0)
        {
            var reservationIds = reservations.Select(row => row.Id).ToArray();
            var allocations = (await _reservationAllocationRepo.FindAsync(row =>
                reservationIds.Contains(row.ReservationId))).ToArray();
            allocationByReservation = allocations
                .GroupBy(row => row.ReservationId)
                .ToDictionary(group => group.Key,
                    group => (IReadOnlyList<StockReservationAllocation>)group.OrderBy(row => row.Ordinal).ToArray());
        }
        var reservationsByLot = new Dictionary<(int ItemId, int LocationId, string? BatchNumber, DateTime? ExpiryDate), int>();
        foreach (var reservation in reservations)
        {
            var allocations = allocationByReservation.GetValueOrDefault(reservation.Id);
            if (allocations is { Count: > 0 })
            {
                foreach (var allocation in allocations)
                {
                    var key = (reservation.ItemId, reservation.LocationId, allocation.BatchNumber,
                        StockLotExpiryDate.Normalize(allocation.ExpiryDate));
                    reservationsByLot[key] = checked(reservationsByLot.GetValueOrDefault(key) + Remaining(allocation));
                }
            }
            else
            {
                var key = (reservation.ItemId, reservation.LocationId, reservation.BatchNumber,
                    StockLotExpiryDate.Normalize(reservation.ExpiryDate));
                reservationsByLot[key] = checked(reservationsByLot.GetValueOrDefault(key) + Remaining(reservation));
            }
        }

        return stock
            .GroupBy(row => (row.ItemId, row.LocationId, row.BatchNumber,
                ExpiryDate: StockLotExpiryDate.Normalize(row.ExpiryDate)))
            .Select(group =>
            {
                var key = group.Key;
                var reserved = reservationsByLot.GetValueOrDefault(key);
                var onHand = group.Sum(row => row.Quantity);
                var quarantined = group.Sum(row => row.QuarantinedQuantity);
                return new StockAvailabilityView(key.ItemId, key.LocationId, key.BatchNumber, key.ExpiryDate,
                    onHand, reserved, quarantined, Math.Max(0, onHand - reserved - quarantined));
            })
            .OrderBy(row => row.ItemId)
            .ThenBy(row => row.LocationId)
            .ThenBy(row => row.ExpiryDate.HasValue ? 0 : 1)
            .ThenBy(row => row.ExpiryDate)
            .ThenBy(row => row.BatchNumber)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StockValuationView>> GetValuationAsync(
        int? itemId = null,
        int? locationId = null,
        IReadOnlyCollection<int>? companyIds = null)
    {
        if (companyIds is { Count: 0 })
            return [];

        IReadOnlyList<StockValuationView> result = [];
        await _unitOfWork.ExecuteInReadSnapshotAsync(async () =>
        {
            var buckets = await _valuationBucketRepo.FindAsync(bucket =>
                (!itemId.HasValue || bucket.ItemId == itemId.Value) &&
                (!locationId.HasValue || bucket.LocationId == locationId.Value) &&
                (companyIds == null ||
                 (bucket.Location.Branch != null && companyIds.Contains(bucket.Location.Branch.CompanyId))));
            var entries = await _valuationEntryRepo.FindAsync(entry =>
                (!itemId.HasValue || entry.ItemId == itemId.Value) &&
                (!locationId.HasValue || entry.LocationId == locationId.Value) &&
                (companyIds == null ||
                 (entry.Location.Branch != null && companyIds.Contains(entry.Location.Branch.CompanyId))));
            var entriesByBucket = entries
                .GroupBy(entry => (entry.ItemId, entry.LocationId))
                .ToDictionary(group => group.Key, group => (IReadOnlyList<StockValuationEntryView>)group
                    .OrderBy(entry => entry.Id)
                    .Select(entry => new StockValuationEntryView(
                        entry.Id,
                        entry.StockTransactionId,
                        entry.EntryType,
                        entry.Quantity,
                        entry.UnitCost,
                        entry.TotalValue))
                    .ToArray());

            result = buckets
                .OrderBy(bucket => bucket.ItemId)
                .ThenBy(bucket => bucket.LocationId)
                .Select(bucket => new StockValuationView(
                    bucket.Id,
                    bucket.ItemId,
                    bucket.LocationId,
                    bucket.Quantity,
                    bucket.Value,
                    entriesByBucket.GetValueOrDefault((bucket.ItemId, bucket.LocationId), [])))
                .ToArray();
        });

        return result;
    }

    private async Task ChangeReservationStateAsync(
        string sourceLineReference,
        StockReservationStatus requestedStatus,
        string? reason,
        StockMutationScope? mutationScope)
    {
        EnsureReservationRepository();
        EnsureSourceLineReference(sourceLineReference);
        EnsureReservationFields(null, reason);
        var source = sourceLineReference.Trim();

        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var initialReservation = await FindReservationAsync(source);
            if (initialReservation is null)
                throw new KeyNotFoundException("Reservation not found.");
            await _unitOfWork.AcquireLocationLocksAsync([initialReservation.LocationId]);
            var reservation = await FindReservationAsync(source);
            if (reservation is null || reservation.LocationId != initialReservation.LocationId)
                throw new StockAvailabilityConflictException("The reservation location changed during the operation.");
            var location = await EnsureLocationUsableAsync(reservation.LocationId);
            await EnsureAuthorizedCompanyScopeAsync(location, mutationScope);
            if (reservation.Status is StockReservationStatus.Released or
                StockReservationStatus.Cancelled or StockReservationStatus.Expired)
                return;
            if (reservation.Status == StockReservationStatus.Consumed)
                throw new StockAvailabilityConflictException("Consumed reservations cannot be released or cancelled.");

            var allocations = await LoadReservationAllocationsAsync(reservation);
            var remaining = allocations.Allocations.Sum(Remaining);
            if (remaining != Remaining(reservation))
                throw new StockAvailabilityConflictException("Stock reservation allocations are inconsistent.");
            var releasedAllocations = allocations.Allocations
                .Where(allocation => Remaining(allocation) > 0)
                .Select(allocation => new
                {
                    allocation.BatchNumber,
                    ExpiryDate = StockLotExpiryDate.Normalize(allocation.ExpiryDate),
                    ReleasedQuantity = Remaining(allocation)
                })
                .ToArray();
            foreach (var allocation in allocations.Allocations.Where(allocation => Remaining(allocation) > 0))
            {
                var stock = await GetByItemAndLocationAsync(
                    reservation.ItemId,
                    reservation.LocationId,
                    allocation.BatchNumber,
                    allocation.ExpiryDate)
                    ?? throw new InvalidOperationException("Reservation stock no longer exists.");
                var allocationRemaining = Remaining(allocation);
                if (stock.ReservedQuantity < allocationRemaining)
                    throw new StockAvailabilityConflictException("Stock reservation counters are inconsistent.");
                stock.ReservedQuantity -= allocationRemaining;
                await _stockRepo.UpdateAsync(stock);
            }

            var now = DateTimeOffset.UtcNow;
            var finalStatus = reservation.ExpiresAt <= now
                ? StockReservationStatus.Expired
                : requestedStatus;
            reservation.Status = finalStatus;
            reservation.ClosedAt = now;
            reservation.ResolutionReason = string.IsNullOrWhiteSpace(reason)
                ? finalStatus.ToString()
                : reason.Trim();
            await _reservationRepo!.UpdateAsync(reservation);
            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext,
                $"Stock.Reservation{finalStatus}", new
                {
                    reservation.ItemId,
                    reservation.LocationId,
                    reservation.SourceLineReference,
                    ReleasedQuantity = remaining,
                    Allocations = releasedAllocations,
                    reservation.ResolutionReason
                }));
            await _unitOfWork.SaveChangesAsync();
        });
    }

    private async Task ReleaseExpiredReservationsAsync(int itemId, int locationId)
    {
        if (_reservationRepo is null)
            return;

        var now = DateTimeOffset.UtcNow;
        var expired = (await _reservationRepo.FindAsync(row =>
            row.ItemId == itemId && row.LocationId == locationId &&
            row.Status == StockReservationStatus.Active && row.ExpiresAt <= now)).ToList();
        if (expired.Count == 0)
            return;

        var releasedByLot = new Dictionary<(int ItemId, int LocationId, string? BatchNumber, DateTime? ExpiryDate), int>();
        var releasedByReservation = new Dictionary<int, IReadOnlyList<StockReservationAllocation>>();
        foreach (var reservation in expired)
        {
            var allocations = await LoadReservationAllocationsAsync(reservation);
            if (allocations.Allocations.Sum(Remaining) != Remaining(reservation))
                throw new StockAvailabilityConflictException("Stock reservation allocations are inconsistent.");
            releasedByReservation[reservation.Id] = allocations.Allocations
                .Where(allocation => Remaining(allocation) > 0)
                .OrderBy(allocation => allocation.Ordinal)
                .ToArray();
            foreach (var allocation in allocations.Allocations)
            {
                var remaining = Remaining(allocation);
                if (remaining <= 0)
                    continue;
                var key = (reservation.ItemId, reservation.LocationId, allocation.BatchNumber,
                    StockLotExpiryDate.Normalize(allocation.ExpiryDate));
                releasedByLot[key] = checked(releasedByLot.GetValueOrDefault(key) + remaining);
            }
        }

        foreach (var released in releasedByLot)
        {
            var stock = await GetByItemAndLocationAsync(
                released.Key.ItemId, released.Key.LocationId, released.Key.BatchNumber, released.Key.ExpiryDate)
                ?? throw new InvalidOperationException("Reservation stock no longer exists.");
            var releaseQuantity = released.Value;
            if (stock.ReservedQuantity < releaseQuantity)
                throw new StockAvailabilityConflictException("Stock reservation counters are inconsistent.");
            stock.ReservedQuantity -= releaseQuantity;
            await _stockRepo.UpdateAsync(stock);
        }

        foreach (var reservation in expired)
        {
            reservation.Status = StockReservationStatus.Expired;
            reservation.ClosedAt = now;
            reservation.ResolutionReason = "Expired";
            await _reservationRepo.UpdateAsync(reservation);
            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext,
                "Stock.ReservationExpired", new
                {
                    reservation.ItemId,
                    reservation.LocationId,
                    reservation.SourceLineReference,
                    ReleasedQuantity = Remaining(reservation),
                    Allocations = releasedByReservation[reservation.Id].Select(allocation => new
                    {
                        allocation.BatchNumber,
                        ExpiryDate = StockLotExpiryDate.Normalize(allocation.ExpiryDate),
                        ReleasedQuantity = Remaining(allocation)
                    }).ToArray()
                }));
        }

        await _unitOfWork.SaveChangesAsync();
        _unitOfWork.ClearTracker();
    }

    private async Task<List<(StockInHand Stock, int Quantity)>> SelectStockLotsForReservationAsync(
        int itemId,
        int locationId,
        string? batchNumber,
        DateTime? expiryDate,
        int requestedQuantity,
        bool allowExpiredLots)
    {
        if (batchNumber is not null || expiryDate.HasValue)
        {
            var requestedStock = await GetStockForRequestedLotAsync(itemId, locationId, batchNumber, expiryDate);
            return requestedStock is null ? [] : [(requestedStock, requestedQuantity)];
        }

        // FEFO is deterministic across all lots. Un-dated stock is last because it
        // cannot win an expiry-first allocation.
        var stockRows = (await _stockRepo.FindAsync(row =>
                row.ItemId == itemId && row.LocationId == locationId))
            .ToArray();
        var ambiguousAvailableLot = stockRows
            .GroupBy(row => (row.BatchNumber, ExpiryDate: StockLotExpiryDate.Normalize(row.ExpiryDate)))
            .FirstOrDefault(group => group.Count() > 1 && group.Any(row =>
                (long)row.Quantity - row.ReservedQuantity - row.QuarantinedQuantity > 0));
        if (ambiguousAvailableLot is not null)
            throw new StockAvailabilityConflictException(
                "More than one stock row matches an available lot; reconcile the lot balance before reserving.");

        var candidates = stockRows
            .Where(row => (long)row.Quantity - row.ReservedQuantity - row.QuarantinedQuantity > 0 &&
                          (allowExpiredLots || !IsExpiredStockLot(row.ExpiryDate)))
            .OrderBy(row => row.ExpiryDate.HasValue ? 0 : 1)
            .ThenBy(row => row.ExpiryDate)
            .ThenBy(row => row.BatchNumber)
            .ThenBy(row => row.Id)
            .ToArray();
        var selected = new List<(StockInHand Stock, int Quantity)>();
        var quantityLeft = requestedQuantity;
        foreach (var candidate in candidates)
        {
            var available = (long)candidate.Quantity - candidate.ReservedQuantity - candidate.QuarantinedQuantity;
            if (available <= 0)
                continue;
            var allocated = (int)Math.Min(quantityLeft, available);
            selected.Add((candidate, allocated));
            quantityLeft -= allocated;
            if (quantityLeft == 0)
                break;
        }

        return selected;
    }

    private async Task<bool> HasEnoughUnexpiredOnHandAsync(int itemId, int locationId, int requestedQuantity)
    {
        var stockRows = await _stockRepo.FindAsync(row => row.ItemId == itemId && row.LocationId == locationId);
        var potentiallyAvailableQuantity = stockRows
            .Where(row => !IsExpiredStockLot(row.ExpiryDate))
            .Sum(row => Math.Max(0L, (long)row.Quantity - row.QuarantinedQuantity));
        return potentiallyAvailableQuantity >= requestedQuantity;
    }

    private async Task<StockReservation?> FindReservationAsync(string sourceLineReference) =>
        (await _reservationRepo!.FindAsync(row => row.SourceLineReference == sourceLineReference)).FirstOrDefault();

    private async Task<LoadedReservationAllocations> LoadReservationAllocationsAsync(StockReservation reservation)
    {
        if (_reservationAllocationRepo is not null)
        {
            var persisted = (await _reservationAllocationRepo.FindAsync(
                    allocation => allocation.ReservationId == reservation.Id))
                .OrderBy(allocation => allocation.Ordinal)
                .ToArray();
            if (persisted.Length > 0)
                return new LoadedReservationAllocations(persisted, true);
        }

        // Rows created before lot allocations were normalized have their single lot
        // directly on StockReservation. Keep those rows readable and mutable.
        return new LoadedReservationAllocations(
            [new StockReservationAllocation
            {
                ReservationId = reservation.Id,
                Ordinal = 0,
                BatchNumber = reservation.BatchNumber,
                ExpiryDate = StockLotExpiryDate.Normalize(reservation.ExpiryDate),
                Quantity = reservation.Quantity,
                ConsumedQuantity = reservation.ConsumedQuantity,
                ExpiryExceptionReason = reservation.ExpiryExceptionReason
            }],
            false);
    }

    private async Task<bool> ReservationReplayReasonMatchesAsync(
        StockReservation reservation,
        string? requestedReason)
    {
        var allocations = await LoadReservationAllocationsAsync(reservation);
        var reasons = allocations.Allocations
            .Select(allocation => allocation.ExpiryExceptionReason)
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Select(reason => reason!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return requestedReason is null
            ? reasons.Length == 0
            : reasons.Length == 1 && string.Equals(reasons[0], requestedReason, StringComparison.Ordinal);
    }

    private static StockReservationView ToView(
        StockReservation reservation,
        IReadOnlyList<StockReservationAllocation> allocations) => new(
        reservation.Id,
        reservation.ItemId,
        reservation.LocationId,
        reservation.SourceLineReference,
        reservation.BatchNumber,
        reservation.ExpiryDate,
        reservation.Quantity,
        reservation.ConsumedQuantity,
        Remaining(reservation),
        reservation.ExpiresAt,
        reservation.Status,
        reservation.ExpiryExceptionReason,
        allocations.Select(allocation => new StockReservationAllocationView(
                allocation.BatchNumber,
                StockLotExpiryDate.Normalize(allocation.ExpiryDate),
                allocation.Quantity,
                allocation.ConsumedQuantity,
                Remaining(allocation),
                allocation.ExpiryExceptionReason))
            .ToArray());

    private static int Remaining(StockReservation reservation) =>
        checked(reservation.Quantity - reservation.ConsumedQuantity);

    private static int Remaining(StockReservationAllocation allocation) =>
        checked(allocation.Quantity - allocation.ConsumedQuantity);

    private static string CreateReservationConsumptionReference(
        string sourceLineReference,
        int consumedBefore,
        int allocationOrdinal)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceLineReference)))[..32];
        var suffix = $":consume:{digest}:{consumedBefore}:{allocationOrdinal}";
        var prefixLength = Math.Max(0, 128 - suffix.Length);
        var prefix = sourceLineReference[..Math.Min(sourceLineReference.Length, prefixLength)];
        return prefix + suffix;
    }

    private static void EnsureAvailable(
        StockInHand stock,
        int quantity,
        string operation,
        int? reservedOverride = null)
    {
        var reserved = reservedOverride ?? stock.ReservedQuantity;
        var available = (long)stock.Quantity - reserved - stock.QuarantinedQuantity;
        if (reserved < 0 || stock.QuarantinedQuantity < 0 || available < quantity)
            throw new StockAvailabilityConflictException(
                $"Insufficient available stock for {operation}; reserved stock or quarantined stock cannot be used.");
    }

    private static void EnsureLotNotExpired(DateTime? expiryDate)
    {
        if (IsExpiredStockLot(expiryDate))
            throw new StockAvailabilityConflictException(
                "The selected stock lot has expired and cannot be restocked.");
    }

    private static async Task<string?> EnsureExpiredLotExceptionAsync(
        DateTime? expiryDate,
        string? reason,
        StockMutationScope? mutationScope)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var isExpired = IsExpiredStockLot(expiryDate);

        if (!isExpired)
        {
            if (normalizedReason is not null)
                throw new StockAvailabilityConflictException(
                    "An expiry exception reason can only be supplied for an expired stock lot.");

            return null;
        }

        if (normalizedReason is null)
            throw new StockAvailabilityConflictException(
                "An audit reason is required to use an expired stock lot.");

        if (mutationScope?.ReauthorizeExpiredStockOverride is not { } reauthorize ||
            !await reauthorize())
            throw new StockAvailabilityConflictException(
                "An explicit company-scoped expired-stock override capability is required.");

        return normalizedReason;
    }

    private static bool IsExpiredStockLot(DateTime? expiryDate) =>
        expiryDate.HasValue &&
        DateOnly.FromDateTime(expiryDate.Value) < DateOnly.FromDateTime(DateTime.UtcNow);

    private static void EnsureExpiryExceptionReasonLength(string? reason)
    {
        if (reason?.Length > 500)
            throw new ArgumentException("Expiry exception reason must be 500 characters or fewer.", nameof(reason));
    }

    private void EnsureReservationRepository()
    {
        if (_reservationRepo is null)
            throw new InvalidOperationException("Stock reservation persistence is not configured.");
    }

    private static void EnsureReservationRequest(int quantity, string sourceLineReference)
    {
        if (quantity <= 0)
            throw new ArgumentException("Reservation quantity must be positive.", nameof(quantity));
        EnsureSourceLineReference(sourceLineReference);
    }

    private static void EnsureReservationFields(string? batchNumber, string? notesOrReason)
    {
        if (batchNumber?.Length > 100)
            throw new ArgumentException("Batch number must be 100 characters or fewer.", nameof(batchNumber));
        if (notesOrReason?.Length > 500)
            throw new ArgumentException("Notes or reason must be 500 characters or fewer.", nameof(notesOrReason));
    }

    private static void EnsureSourceLineReference(string sourceLineReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLineReference);
        if (sourceLineReference.Trim().Length > 128)
            throw new ArgumentException("Source line reference must be 128 characters or fewer.", nameof(sourceLineReference));
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

    private async Task ApplyReturnValuationAsync(
        int itemId,
        int locationId,
        int quantity,
        decimal unitCost,
        StockTransaction source)
    {
        var existing = (await _valuationBucketRepo.FindAsync(bucket =>
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

        await _valuationEntryRepo.AddAsync(new StockValuationEntry
        {
            StockTransaction = source,
            ItemId = itemId,
            LocationId = locationId,
            EntryType = StockValuationEntryType.Return,
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

    private async Task<Location> EnsureLocationUsableAsync(
        int locationId,
        CancellationToken cancellationToken = default)
    {
        if (!_tenantContext.IsResolved)
            throw new InvalidOperationException("A tenant context is required for stock operations.");

        // Filtered queries apply tenant and soft-delete predicates even when this DbContext
        // has already tracked an entity with the requested key.
        var locationRows = cancellationToken.CanBeCanceled
            ? await _locationRepo.FindAsync(candidate => candidate.Id == locationId, cancellationToken)
            : await _locationRepo.FindAsync(candidate => candidate.Id == locationId);
        var location = locationRows.FirstOrDefault();
        if (location is null || location.IsDeleted ||
            !string.Equals(location.TenantId, _tenantContext.TenantId, StringComparison.Ordinal))
            throw new InvalidOperationException("Location does not exist in the current tenant or is deleted.");

        if (location.BranchId is not int branchId)
            return location;

        var branchRows = cancellationToken.CanBeCanceled
            ? await _branchRepo.FindAsync(candidate => candidate.Id == branchId, cancellationToken)
            : await _branchRepo.FindAsync(candidate => candidate.Id == branchId);
        var branch = branchRows.FirstOrDefault();
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

    private static async Task EnsureAuthorizedCompanyScopeAsync(
        Location location,
        StockMutationScope? mutationScope)
    {
        if (mutationScope is not StockMutationScope expected)
            return;

        var currentCompanyId = location.Branch?.CompanyId;
        if (currentCompanyId != expected.CompanyId)
        {
            throw new UnauthorizedAccessException(
                "Location ownership changed while stock access was being authorized. Refresh access and retry.");
        }

        if (expected.Reauthorize is not null && !await expected.Reauthorize())
        {
            throw new UnauthorizedAccessException(
                "Company posting access changed before the stock mutation could be committed.");
        }
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

    private async Task<bool> VerifyReservationConsumptionAsync(string sourceLineReference, int expectedConsumedQuantity)
    {
        var reservation = await FindReservationAsync(sourceLineReference);
        if (reservation is not null && reservation.ConsumedQuantity >= expectedConsumedQuantity)
            return true;

        _unitOfWork.ClearTracker();
        return false;
    }

    private sealed record LoadedReservationAllocations(
        IReadOnlyList<StockReservationAllocation> Allocations,
        bool Persisted);
}
