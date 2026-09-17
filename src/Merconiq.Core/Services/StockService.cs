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
        IRepository<StockReservation>? reservationRepo = null)
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
        expiryDate = StockLotExpiryDate.Normalize(expiryDate);
        IEnumerable<StockInHand> results;
        if (expiryDate is DateTime expiryDayStart)
        {
            var expiryDayEnd = expiryDayStart.AddDays(1);
            results = await _stockRepo.FindAsync(s =>
                s.ItemId == itemId &&
                s.LocationId == locationId &&
                s.BatchNumber == batchNumber &&
                s.ExpiryDate >= expiryDayStart &&
                s.ExpiryDate < expiryDayEnd);
        }
        else
        {
            results = await _stockRepo.FindAsync(s =>
                s.ItemId == itemId &&
                s.LocationId == locationId &&
                s.BatchNumber == batchNumber &&
                s.ExpiryDate == null);
        }

        var matchingRows = results.Take(2).ToArray();
        if (matchingRows.Length > 1)
            throw new StockAvailabilityConflictException(
                "Multiple stock rows match the same lot expiry date; reconcile inventory before changing it.");

        return matchingRows.FirstOrDefault();
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
                    // Keep stock postings in the same lock domain as company currency
                    // freeze checks. ponytail: tenant-wide serialization is the smallest
                    // correct boundary; split by company if throughput requires it.
                    await _unitOfWork.AcquireTenantOperationLockAsync("organization-state");
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
        StockMutationScope? mutationScope = null)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive");
        if (fromLocationId == toLocationId) throw new ArgumentException("Source and destination must be different");
        expiryDate = StockLotExpiryDate.Normalize(expiryDate);
        EnsureLotNotExpired(expiryDate);

        StockTransaction? transaction = null;
        await ExecuteWithRetryAsync(itemId, async () =>
        {
            await _unitOfWork.AcquireLocationLocksAsync([fromLocationId, toLocationId]);
            var sourceLocation = await EnsureLocationUsableAsync(fromLocationId);
            var destinationLocation = await EnsureLocationUsableAsync(toLocationId);
            await EnsureAuthorizedCompanyScopeAsync(sourceLocation, mutationScope);
            await EnsureAuthorizedCompanyScopeAsync(destinationLocation, mutationScope);
            await EnsureSameCompanyTransferAsync(sourceLocation, destinationLocation);
            await ReleaseExpiredReservationsAsync(itemId, fromLocationId);
            await ReleaseExpiredReservationsAsync(itemId, toLocationId);
            var source = await GetByItemAndLocationAsync(itemId, fromLocationId, batchNumber, expiryDate);
            if (source == null || source.Quantity < quantity)
                throw new InvalidOperationException("Insufficient stock at source location");
            EnsureAvailable(source, quantity, "transfer");

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
    public async Task SellStockAsync(
        int itemId,
        int locationId,
        int quantity,
        string? notes,
        string? batchNumber = null,
        DateTime? expiryDate = null,
        string? reservationSourceLineReference = null,
        StockMutationScope? mutationScope = null)
    {
        if (quantity <= 0) throw new ArgumentException("Quantity must be positive");
        expiryDate = StockLotExpiryDate.Normalize(expiryDate);
        EnsureLotNotExpired(expiryDate);

        StockTransaction? transaction = null;
        await ExecuteWithRetryAsync(itemId, async () =>
        {
            await _unitOfWork.AcquireLocationLocksAsync([locationId]);
            var location = await EnsureLocationUsableAsync(locationId);
            await EnsureAuthorizedCompanyScopeAsync(location, mutationScope);
            await ReleaseExpiredReservationsAsync(itemId, locationId);
            var stock = await GetByItemAndLocationAsync(itemId, locationId, batchNumber, expiryDate);
            if (stock == null || stock.Quantity < quantity)
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
                if (reservation.ItemId != itemId || reservation.LocationId != locationId ||
                    reservation.BatchNumber != batchNumber || reservation.ExpiryDate != expiryDate)
                    throw new StockAvailabilityConflictException("Reservation does not match the requested stock lot.");

                reservationRemaining = Remaining(reservation);
                if (reservationRemaining < quantity)
                    throw new StockAvailabilityConflictException("The reservation does not contain enough remaining quantity.");

                var reservedForOtherLines = stock.ReservedQuantity - reservationRemaining;
                if (reservedForOtherLines < 0)
                    throw new StockAvailabilityConflictException("Stock reservation counters are inconsistent.");
                EnsureAvailable(stock, quantity, "reservation consumption", reservedForOtherLines);
                stock.ReservedQuantity -= quantity;
                reservation.ConsumedQuantity = checked(reservation.ConsumedQuantity + quantity);
                if (Remaining(reservation) == 0)
                {
                    reservation.Status = StockReservationStatus.Consumed;
                    reservation.ClosedAt = DateTimeOffset.UtcNow;
                    reservation.ResolutionReason = "Consumed";
                }
                await _reservationRepo!.UpdateAsync(reservation);
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

    /// <inheritdoc />
    public async Task CreateReservationAsync(
        CreateStockReservationRequest request,
        StockMutationScope? mutationScope = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with { ExpiryDate = StockLotExpiryDate.Normalize(request.ExpiryDate) };
        EnsureReservationRequest(request.Quantity, request.SourceLineReference);
        EnsureReservationFields(request.BatchNumber, null);
        EnsureReservationRepository();

        var sourceLineReference = request.SourceLineReference.Trim();
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
                     existing.BatchNumber == request.BatchNumber && existing.ExpiryDate == request.ExpiryDate))
                    return;

                if (existing.Status == StockReservationStatus.Active && existing.ExpiresAt <= now)
                    await ReleaseExpiredReservationsAsync(request.ItemId, request.LocationId);

                throw new InvalidOperationException("The source line already has a different or closed reservation.");
            }

            EnsureLotNotExpired(request.ExpiryDate);
            if (request.BatchNumber is null && !request.ExpiryDate.HasValue)
            {
                var selectedLot = await SelectStockForReservationAsync(
                    request.ItemId, request.LocationId, request.BatchNumber, request.ExpiryDate);
                if (selectedLot is not null)
                    EnsureLotNotExpired(selectedLot.ExpiryDate);
            }
            await ReleaseExpiredReservationsAsync(request.ItemId, request.LocationId);

            var stock = await SelectStockForReservationAsync(request.ItemId, request.LocationId,
                request.BatchNumber, request.ExpiryDate);
            if (stock is null)
                throw new StockAvailabilityConflictException("No stock is available for the requested lot.");
            EnsureAvailable(stock, request.Quantity, "reservation");

            var reservation = new StockReservation
            {
                ItemId = request.ItemId,
                LocationId = request.LocationId,
                SourceLineReference = sourceLineReference,
                BatchNumber = stock.BatchNumber,
                ExpiryDate = stock.ExpiryDate,
                Quantity = request.Quantity,
                ExpiresAt = expiresAt
            };
            stock.ReservedQuantity = checked(stock.ReservedQuantity + request.Quantity);
            await _stockRepo.UpdateAsync(stock);
            await (_reservationRepo ?? throw new InvalidOperationException(
                "Stock reservation persistence is not configured.")).AddAsync(reservation);
            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext, "Stock.Reserved",
                new { reservation.ItemId, reservation.LocationId, reservation.SourceLineReference, reservation.Quantity, reservation.BatchNumber, reservation.ExpiryDate, reservation.ExpiresAt }));
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
        var reservation = await GetReservationAsync(sourceLineReference)
            ?? throw new KeyNotFoundException("Reservation not found.");
        if (reservation.Status != StockReservationStatus.Active)
            throw new StockAvailabilityConflictException("Reservation is not active.");
        EnsureLotNotExpired(reservation.ExpiryDate);
        if (reservation.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await ReleaseReservationAsync(sourceLineReference, "Expired", mutationScope);
            throw new StockAvailabilityConflictException("Reservation has expired.");
        }
        if (reservation.RemainingQuantity < request.Quantity)
            throw new StockAvailabilityConflictException("The reservation does not contain enough remaining quantity.");

        await SellStockAsync(
            reservation.ItemId,
            reservation.LocationId,
            request.Quantity,
            request.Notes,
            reservation.BatchNumber,
            reservation.ExpiryDate,
            sourceLineReference,
            mutationScope);
    }

    /// <inheritdoc />
    public async Task<StockReservationView?> GetReservationAsync(string sourceLineReference)
    {
        EnsureReservationRepository();
        if (string.IsNullOrWhiteSpace(sourceLineReference))
            throw new ArgumentException("Source line reference is required.", nameof(sourceLineReference));
        var reservation = await FindReservationAsync(sourceLineReference.Trim());
        return reservation is null ? null : ToView(reservation);
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
        var reservations = await _reservationRepo!.FindAsync(row =>
            row.Status == StockReservationStatus.Active && row.ExpiresAt > now &&
            (!itemId.HasValue || row.ItemId == itemId.Value) &&
            (!locationId.HasValue || row.LocationId == locationId.Value));
        var reservedByLot = reservations
            .GroupBy(row => (row.ItemId, row.LocationId, row.BatchNumber,
                ExpiryDate: StockLotExpiryDate.Normalize(row.ExpiryDate)))
            .ToDictionary(group => group.Key, group => group.Sum(Remaining));

        return stock
            .GroupBy(row => (row.ItemId, row.LocationId, row.BatchNumber,
                ExpiryDate: StockLotExpiryDate.Normalize(row.ExpiryDate)))
            .Select(group =>
            {
                var key = group.Key;
                var reserved = reservedByLot.GetValueOrDefault(key);
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

            var remaining = Remaining(reservation);
            var stock = await GetByItemAndLocationAsync(
                reservation.ItemId, reservation.LocationId, reservation.BatchNumber, reservation.ExpiryDate)
                ?? throw new InvalidOperationException("Reservation stock no longer exists.");
            if (stock.ReservedQuantity < remaining)
                throw new StockAvailabilityConflictException("Stock reservation counters are inconsistent.");

            var now = DateTimeOffset.UtcNow;
            var finalStatus = reservation.ExpiresAt <= now
                ? StockReservationStatus.Expired
                : requestedStatus;
            stock.ReservedQuantity -= remaining;
            reservation.Status = finalStatus;
            reservation.ClosedAt = now;
            reservation.ResolutionReason = string.IsNullOrWhiteSpace(reason)
                ? finalStatus.ToString()
                : reason.Trim();
            await _stockRepo.UpdateAsync(stock);
            await _reservationRepo!.UpdateAsync(reservation);
            await _webhookDispatcher.EnqueueAsync(WebhookEventFactory.Create(_tenantContext,
                $"Stock.Reservation{finalStatus}", new
                {
                    reservation.ItemId,
                    reservation.LocationId,
                    reservation.SourceLineReference,
                    ReleasedQuantity = remaining,
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

        foreach (var group in expired.GroupBy(row =>
                     (row.ItemId, row.LocationId, row.BatchNumber, row.ExpiryDate)))
        {
            var stock = await GetByItemAndLocationAsync(
                group.Key.ItemId, group.Key.LocationId, group.Key.BatchNumber, group.Key.ExpiryDate)
                ?? throw new InvalidOperationException("Reservation stock no longer exists.");
            var released = group.Sum(Remaining);
            if (stock.ReservedQuantity < released)
                throw new StockAvailabilityConflictException("Stock reservation counters are inconsistent.");
            stock.ReservedQuantity -= released;
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
                    ReleasedQuantity = Remaining(reservation)
                }));
        }

        await _unitOfWork.SaveChangesAsync();
        _unitOfWork.ClearTracker();
    }

    private async Task<StockInHand?> SelectStockForReservationAsync(
        int itemId,
        int locationId,
        string? batchNumber,
        DateTime? expiryDate)
    {
        if (batchNumber is not null || expiryDate.HasValue)
            return await GetByItemAndLocationAsync(itemId, locationId, batchNumber, expiryDate);

        // FEFO is deterministic for the bounded single-lot reservation contract. A null
        // expiry is last because it cannot win an expiry-first allocation.
        return (await _stockRepo.FindAsync(row =>
                row.ItemId == itemId && row.LocationId == locationId &&
                row.Quantity - row.ReservedQuantity - row.QuarantinedQuantity > 0))
            .OrderBy(row => row.ExpiryDate.HasValue ? 0 : 1)
            .ThenBy(row => row.ExpiryDate)
            .ThenBy(row => row.BatchNumber)
            .ThenBy(row => row.Id)
            .FirstOrDefault();
    }

    private async Task<StockReservation?> FindReservationAsync(string sourceLineReference) =>
        (await _reservationRepo!.FindAsync(row => row.SourceLineReference == sourceLineReference)).FirstOrDefault();

    private static StockReservationView ToView(StockReservation reservation) => new(
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
        reservation.Status);

    private static int Remaining(StockReservation reservation) =>
        checked(reservation.Quantity - reservation.ConsumedQuantity);

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
        if (expiryDate.HasValue &&
            DateOnly.FromDateTime(expiryDate.Value) < DateOnly.FromDateTime(DateTime.UtcNow))
            throw new StockAvailabilityConflictException(
                "The selected stock lot has expired and cannot be reserved, sold, or transferred.");
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
}
