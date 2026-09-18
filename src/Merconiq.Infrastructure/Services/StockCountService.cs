using Merconiq.Core.Entities;
using Merconiq.Core.Exceptions;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Infrastructure.Services;

/// <summary>Persists physical-count baselines and detects later movements per item/lot bucket.</summary>
public sealed class StockCountService(
    InventoryDbContext context,
    IUnitOfWork unitOfWork,
    IStockService? stockService = null) : IStockCountService
{
    public async Task<StockCountView> StartAsync(
        int locationId,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(locationId);

        StockCountView? result = null;
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            result = null;
            await unitOfWork.AcquireLocationLocksAsync([locationId], cancellationToken);

            var location = await context.Locations
                .Include(candidate => candidate.Branch)
                .SingleOrDefaultAsync(candidate => candidate.Id == locationId, cancellationToken);
            if (location is null)
                throw new KeyNotFoundException("The stock-count location was not found.");

            await EnsureAuthorizedLocationAsync(location, mutationScope, cancellationToken);

            var watermark = await context.StockTransactions
                .Where(transaction => transaction.FromLocationId == locationId
                    || transaction.ToLocationId == locationId)
                .Select(transaction => (int?)transaction.Id)
                .MaxAsync(cancellationToken) ?? 0;

            var positions = await context.StockInHand
                .AsNoTracking()
                .Include(stock => stock.Item)
                .Where(stock => stock.LocationId == locationId)
                .OrderBy(stock => stock.ItemId)
                .ThenBy(stock => stock.BatchNumber)
                .ThenBy(stock => stock.ExpiryDate)
                .ToListAsync(cancellationToken);

            var positionKeys = new HashSet<StockPositionKey>();
            if (positions.Any(stock => !positionKeys.Add(
                    new StockPositionKey(stock.ItemId, stock.BatchNumber, stock.ExpiryDate))))
            {
                throw new StockAvailabilityConflictException(
                    "Multiple stock rows match the same item/lot bucket; reconcile inventory before starting a count.");
            }

            var count = new StockCount
            {
                LocationId = locationId,
                CompanyId = location.Branch?.CompanyId,
                SnapshotAtUtc = DateTime.UtcNow,
                MovementWatermark = watermark,
                Lines = positions.Select(stock => new StockCountLine
                {
                    ItemId = stock.ItemId,
                    ItemCodeSnapshot = stock.Item?.ItemCode ?? $"item-{stock.ItemId}",
                    ItemDescriptionSnapshot = stock.Item?.Description ?? string.Empty,
                    BatchNumber = stock.BatchNumber,
                    ExpiryDate = stock.ExpiryDate,
                    SnapshotQuantity = stock.Quantity
                }).ToList()
            };

            context.StockCounts.Add(count);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            result = new StockCountView(
                count.Id,
                count.LocationId,
                count.CompanyId,
                count.SnapshotAtUtc,
                false,
                count.Lines.Select(line => ToView(
                    line,
                    line.SnapshotQuantity,
                    countedQuantity: null,
                    countedAtUtc: null,
                    countedBy: null,
                    movementDetected: false)).ToArray());
        }, cancellationToken);

        return result ?? throw new InvalidOperationException("The stock-count snapshot was not created.");
    }

    public async Task<StockCountView?> GetAsync(
        int countId,
        IReadOnlyCollection<int>? companyIds = null,
        CancellationToken cancellationToken = default)
    {
        if (countId <= 0 || companyIds is { Count: 0 })
            return null;

        StockCountView? result = null;
        await unitOfWork.ExecuteInReadSnapshotAsync(async () =>
        {
            var query = context.StockCounts
                .AsNoTracking()
                .Include(count => count.Location)
                    .ThenInclude(location => location.Branch)
                .Include(count => count.Lines)
                    .ThenInclude(line => line.Observation)
                .Include(count => count.Lines)
                    .ThenInclude(line => line.Variance)
                .Where(count => count.Id == countId);

            if (companyIds is not null)
            {
                query = query.Where(count => count.CompanyId.HasValue
                    && companyIds.Contains(count.CompanyId.Value));
            }

            var count = await query.SingleOrDefaultAsync(cancellationToken);
            if (count is null || count.Location.Branch?.CompanyId != count.CompanyId)
                return;

            result = await BuildViewAsync(count, cancellationToken);
        }, cancellationToken);

        return result;
    }

    public async Task<StockCountAuthorizationContext?> GetAuthorizationContextAsync(
        int countId,
        CancellationToken cancellationToken = default)
    {
        if (countId <= 0)
            return null;

        return await context.StockCounts
            .AsNoTracking()
            .Where(count => count.Id == countId)
            .Select(count => new StockCountAuthorizationContext(count.LocationId, count.CompanyId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<StockCountReconciliationView?> GetReconciliationAsync(
        StockCountReconciliationRequest request,
        IReadOnlyCollection<int>? companyIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.LocationId);
        if (request.ItemId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Item id must be positive when supplied.");
        if (companyIds is { Count: 0 })
            return null;

        var requestedBatch = string.IsNullOrWhiteSpace(request.BatchNumber)
            ? null
            : request.BatchNumber.Trim();
        var requestedExpiry = StockLotExpiryDate.Normalize(request.ExpiryDate);
        StockCountReconciliationView? result = null;
        await unitOfWork.ExecuteInReadSnapshotAsync(async () =>
        {
            result = null;
            var location = await context.Locations
                .AsNoTracking()
                .Include(candidate => candidate.Branch)
                .SingleOrDefaultAsync(candidate => candidate.Id == request.LocationId, cancellationToken);
            if (location is null)
                return;

            var currentCompanyId = location.Branch?.CompanyId;
            if ((request.CompanyId.HasValue && request.CompanyId != currentCompanyId)
                || (companyIds is not null
                    && (!currentCompanyId.HasValue || !companyIds.Contains(currentCompanyId.Value))))
            {
                return;
            }

            var stockQuery = context.StockInHand
                .AsNoTracking()
                .Include(stock => stock.Item)
                .Where(stock => stock.LocationId == request.LocationId);
            var transactionQuery = context.StockTransactions
                .AsNoTracking()
                .Include(transaction => transaction.Item)
                .Where(transaction => transaction.FromLocationId == request.LocationId
                    || transaction.ToLocationId == request.LocationId);
            if (request.ItemId is int itemId)
            {
                stockQuery = stockQuery.Where(stock => stock.ItemId == itemId);
                transactionQuery = transactionQuery.Where(transaction => transaction.ItemId == itemId);
            }
            if (requestedBatch is not null)
            {
                stockQuery = stockQuery.Where(stock => stock.BatchNumber == requestedBatch);
                transactionQuery = transactionQuery.Where(transaction => transaction.BatchNumber == requestedBatch);
            }
            if (requestedExpiry.HasValue)
            {
                stockQuery = stockQuery.Where(stock => stock.ExpiryDate == requestedExpiry);
                transactionQuery = transactionQuery.Where(transaction => transaction.ExpiryDate == requestedExpiry);
            }

            var stockRows = await stockQuery.ToListAsync(cancellationToken);
            var transactions = await transactionQuery
                .OrderBy(transaction => transaction.TransactionDate)
                .ThenBy(transaction => transaction.Id)
                .ToListAsync(cancellationToken);
            var currentByPosition = new Dictionary<StockPositionKey, StockInHand>();
            foreach (var stock in stockRows)
            {
                var key = new StockPositionKey(
                    stock.ItemId,
                    stock.BatchNumber,
                    StockLotExpiryDate.Normalize(stock.ExpiryDate));
                if (!currentByPosition.TryAdd(key, stock))
                    throw new StockAvailabilityConflictException(
                        "Multiple stock rows match the same item/lot bucket; reconcile inventory before reading the ledger.");
            }

            var transactionsByPosition = transactions
                .GroupBy(transaction => new StockPositionKey(
                    transaction.ItemId,
                    transaction.BatchNumber,
                    StockLotExpiryDate.Normalize(transaction.ExpiryDate)))
                .ToDictionary(group => group.Key, group => group.ToArray());
            var positions = currentByPosition.Keys
                .Concat(transactionsByPosition.Keys)
                .Distinct()
                .OrderBy(key => key.ItemId)
                .ThenBy(key => key.BatchNumber)
                .ThenBy(key => key.ExpiryDate)
                .ToArray();

            if (positions.Length == 0)
            {
                result = new StockCountReconciliationView(
                    location.Id,
                    currentCompanyId,
                    DateTime.UtcNow,
                    []);
                return;
            }

            var itemIds = positions.Select(key => key.ItemId).Distinct().ToArray();
            var items = await context.Items
                .AsNoTracking()
                .Where(item => itemIds.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken);
            var valuationBuckets = await context.StockValuationBuckets
                .AsNoTracking()
                .Where(bucket => bucket.LocationId == request.LocationId && itemIds.Contains(bucket.ItemId))
                .ToDictionaryAsync(bucket => bucket.ItemId, cancellationToken);
            var valuationEntries = await context.StockValuationEntries
                .AsNoTracking()
                .Where(entry => entry.LocationId == request.LocationId && itemIds.Contains(entry.ItemId))
                .ToDictionaryAsync(entry => entry.StockTransactionId, cancellationToken);
            var positionViews = positions.Select(key =>
            {
                currentByPosition.TryGetValue(key, out var stock);
                transactionsByPosition.TryGetValue(key, out var positionTransactions);
                positionTransactions ??= [];
                var ledger = positionTransactions.Select(transaction =>
                {
                    valuationEntries.TryGetValue(transaction.Id, out var valuation);
                    var quantityDelta = GetSignedQuantityDelta(transaction, request.LocationId);
                    return new StockCountReconciliationMovementView(
                        transaction.Id,
                        transaction.TransactionDate,
                        transaction.TransactionType,
                        quantityDelta,
                        valuation?.UnitCost ?? transaction.UnitCost,
                        valuation is null ? null : GetSignedValueDelta(transaction, valuation, request.LocationId),
                        transaction.SourceLineReference,
                        transaction.Notes);
                }).ToArray();

                var ledgerQuantity = ledger.Sum(movement => movement.QuantityDelta);
                var onHandQuantity = stock?.Quantity ?? 0;
                var isUnbatched = key.BatchNumber is null && key.ExpiryDate is null;
                valuationBuckets.TryGetValue(key.ItemId, out var bucket);
                (StockTransaction Transaction, StockValuationEntry Entry)[] positionEntries = isUnbatched
                    ? positionTransactions
                        .Where(transaction => valuationEntries.ContainsKey(transaction.Id))
                        .Select(transaction => (Transaction: transaction, Entry: valuationEntries[transaction.Id]))
                        .Where(pair => pair.Entry.LocationId == request.LocationId)
                        .ToArray()
                    : [];
                var valuationTracked = isUnbatched && (bucket is not null || positionEntries.Length > 0);
                var valuationLedgerQuantity = valuationTracked
                    ? positionEntries.Sum(pair => GetValuationDirection(pair.Transaction, pair.Entry, request.LocationId)
                        * pair.Entry.Quantity)
                    : (int?)null;
                var valuationLedgerValue = valuationTracked
                    ? positionEntries.Sum(pair => GetValuationDirection(pair.Transaction, pair.Entry, request.LocationId)
                        * pair.Entry.TotalValue)
                    : (decimal?)null;
                var bucketQuantity = valuationTracked ? bucket?.Quantity ?? 0 : (int?)null;
                var bucketValue = valuationTracked ? bucket?.Value ?? 0m : (decimal?)null;
                var item = items.GetValueOrDefault(key.ItemId);

                return new StockCountReconciliationPositionView(
                    key.ItemId,
                    item?.ItemCode ?? $"item-{key.ItemId}",
                    item?.Description ?? string.Empty,
                    key.BatchNumber,
                    key.ExpiryDate,
                    onHandQuantity,
                    stock?.ReservedQuantity ?? 0,
                    stock?.QuarantinedQuantity ?? 0,
                    onHandQuantity - (stock?.ReservedQuantity ?? 0) - (stock?.QuarantinedQuantity ?? 0),
                    ledgerQuantity,
                    onHandQuantity - ledgerQuantity,
                    valuationTracked,
                    bucketQuantity,
                    bucketValue,
                    valuationLedgerQuantity,
                    valuationLedgerValue,
                    isUnbatched ? onHandQuantity - (bucket?.Quantity ?? 0) : null,
                    valuationTracked ? bucketQuantity - valuationLedgerQuantity : null,
                    valuationTracked ? bucketValue - valuationLedgerValue : null,
                    ledger);
            }).ToArray();

            result = new StockCountReconciliationView(
                location.Id,
                currentCompanyId,
                DateTime.UtcNow,
                positionViews);
        }, cancellationToken);

        return result;
    }

    public async Task<StockCountLineView?> RecordObservationAsync(
        int countId,
        int lineId,
        int countedQuantity,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken = default)
    {
        if (countId <= 0)
            return null;
        ArgumentOutOfRangeException.ThrowIfNegative(lineId);
        ArgumentOutOfRangeException.ThrowIfNegative(countedQuantity);

        var authorizationContext = await GetAuthorizationContextAsync(countId, cancellationToken);
        if (authorizationContext is null)
            return null;

        StockCountLineView? result = null;
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            result = null;
            await unitOfWork.AcquireLocationLocksAsync([authorizationContext.LocationId], cancellationToken);

            var count = await context.StockCounts
                .Include(candidate => candidate.Location)
                    .ThenInclude(location => location.Branch)
                .Include(candidate => candidate.Lines)
                    .ThenInclude(line => line.Observation)
                .Include(candidate => candidate.Lines)
                    .ThenInclude(line => line.Variance)
                .SingleOrDefaultAsync(candidate => candidate.Id == countId, cancellationToken);
            if (count is null || count.Location.Branch?.CompanyId != count.CompanyId)
                return;

            await EnsureAuthorizedLocationAsync(count.Location, mutationScope, cancellationToken);
            var line = count.Lines.SingleOrDefault(candidate => candidate.Id == lineId);
            if (line is null)
                return;

            if (line.Observation is not null)
            {
                if (line.Observation.CountedQuantity != countedQuantity)
                {
                    throw new InvalidOperationException(
                        "A stock-count observation is immutable once recorded.");
                }
            }
            else
            {
                var observation = new StockCountObservation
                {
                    StockCountLineId = line.Id,
                    CountedQuantity = countedQuantity
                };
                context.StockCountObservations.Add(observation);
                line.Observation = observation;
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            var view = await BuildViewAsync(count, cancellationToken);
            result = view.Lines.Single(candidate => candidate.Id == lineId);
        }, cancellationToken);

        return result;
    }

    public async Task<StockCountLineView?> PostVarianceAsync(
        int countId,
        int lineId,
        string reason,
        decimal? approvedUnitCost,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken = default)
    {
        if (countId <= 0)
            return null;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        reason = reason.Trim();
        if (reason.Length > 500)
            throw new ArgumentException("A count-adjustment reason must be 500 characters or fewer.", nameof(reason));
        if (approvedUnitCost is < 0)
            throw new ArgumentOutOfRangeException(nameof(approvedUnitCost), "Approved unit cost must be non-negative.");
        approvedUnitCost = approvedUnitCost is decimal suppliedUnitCost
            ? decimal.Round(suppliedUnitCost, 6, MidpointRounding.AwayFromZero)
            : null;

        var authorizationContext = await GetAuthorizationContextAsync(countId, cancellationToken);
        if (authorizationContext is null)
            return null;

        StockCountLineView? result = null;
        int? expectedCountedQuantity = null;
        await unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            result = null;
            await unitOfWork.AcquireLocationLocksAsync([authorizationContext.LocationId], cancellationToken);

            var count = await context.StockCounts
                .Include(candidate => candidate.Location)
                    .ThenInclude(location => location.Branch)
                .Include(candidate => candidate.Lines)
                    .ThenInclude(line => line.Observation)
                .Include(candidate => candidate.Lines)
                    .ThenInclude(line => line.Variance)
                .SingleOrDefaultAsync(candidate => candidate.Id == countId, cancellationToken);
            if (count is null || count.Location.Branch?.CompanyId != count.CompanyId)
                return;

            await EnsureAuthorizedLocationAsync(count.Location, mutationScope, cancellationToken);
            var line = count.Lines.SingleOrDefault(candidate => candidate.Id == lineId);
            if (line is null)
                return;

            if (line.Variance is { } existingVariance)
            {
                if (!string.Equals(existingVariance.Reason, reason, StringComparison.Ordinal)
                    || existingVariance.ApprovedUnitCost != approvedUnitCost)
                {
                    throw new InvalidOperationException(
                        "A stock-count variance is immutable once posted; the replay payload does not match.");
                }

                var existingView = await BuildViewAsync(count, cancellationToken);
                result = existingView.Lines.Single(candidate => candidate.Id == lineId);
                return;
            }

            var observation = line.Observation
                ?? throw new InvalidOperationException("Record a physical-count observation before posting its variance.");
            expectedCountedQuantity = observation.CountedQuantity;
            var currentStockRows = await context.StockInHand
                .AsNoTracking()
                .Where(stock => stock.LocationId == count.LocationId
                    && stock.ItemId == line.ItemId
                    && stock.BatchNumber == line.BatchNumber
                    && stock.ExpiryDate == line.ExpiryDate)
                .Take(2)
                .ToListAsync(cancellationToken);
            if (currentStockRows.Count > 1)
                throw new StockAvailabilityConflictException(
                    "Multiple stock rows match this count line; reconcile inventory before posting its variance.");

            var currentQuantity = currentStockRows.SingleOrDefault()?.Quantity ?? 0;
            if (currentQuantity != line.SnapshotQuantity)
                throw new StockAvailabilityConflictException(
                    "Stock changed after the physical count; take a new count before posting this variance.");

            var laterMovementExists = await context.StockTransactions.AnyAsync(transaction =>
                transaction.Id > count.MovementWatermark
                && transaction.ItemId == line.ItemId
                && transaction.BatchNumber == line.BatchNumber
                && transaction.ExpiryDate == line.ExpiryDate
                && (transaction.FromLocationId == count.LocationId
                    || transaction.ToLocationId == count.LocationId), cancellationToken);
            if (laterMovementExists)
                throw new StockAvailabilityConflictException(
                    "A stock movement occurred during the count; take a new count before posting this variance.");

            var delta = checked(observation.CountedQuantity - currentQuantity);
            if (delta == 0 && approvedUnitCost.HasValue)
                throw new ArgumentException("Unit cost is not accepted for a zero-quantity variance.", nameof(approvedUnitCost));

            StockCountMovementResult? movement = null;
            if (delta != 0)
            {
                var movementService = stockService
                    ?? throw new InvalidOperationException("Stock movement services are not configured for count adjustments.");
                movement = await movementService.PostStockCountAdjustmentAsync(
                    new StockCountMovementRequest(
                        line.ItemId,
                        count.LocationId,
                        currentQuantity,
                        observation.CountedQuantity,
                        line.BatchNumber,
                        line.ExpiryDate,
                        reason,
                        approvedUnitCost,
                        $"stock-count:{count.Id}:line:{line.Id}"),
                    mutationScope,
                    cancellationToken);
            }

            var variance = new StockCountVariance
            {
                StockCountLineId = line.Id,
                ExpectedCurrentQuantity = currentQuantity,
                CountedQuantity = observation.CountedQuantity,
                DeltaQuantity = delta,
                Reason = reason,
                ApprovedUnitCost = approvedUnitCost,
                ValueAdjustment = delta == 0 ? 0m : movement?.SignedValueAdjustment,
                StockTransactionId = movement?.StockTransactionId
            };
            context.StockCountVariances.Add(variance);
            line.Variance = variance;
            await unitOfWork.SaveChangesAsync(cancellationToken);

            var view = await BuildViewAsync(count, cancellationToken);
            result = view.Lines.Single(candidate => candidate.Id == lineId);
        }, cancellationToken, async () => expectedCountedQuantity.HasValue
            && await context.StockCountVariances.AnyAsync(variance =>
                variance.StockCountLineId == lineId
                && variance.CountedQuantity == expectedCountedQuantity.Value
                && variance.Reason == reason
                && variance.ApprovedUnitCost == approvedUnitCost, CancellationToken.None));

        return result;
    }

    private async Task EnsureAuthorizedLocationAsync(
        Location location,
        StockMutationScope mutationScope,
        CancellationToken cancellationToken)
    {
        if (location.Branch?.CompanyId != mutationScope.CompanyId)
        {
            throw new UnauthorizedAccessException(
                "Location company ownership changed; reauthorize the stock-count operation.");
        }

        if (mutationScope.Reauthorize is not null && !await mutationScope.Reauthorize(cancellationToken))
            throw new UnauthorizedAccessException("The stock-count operation is not authorized.");
    }

    private async Task<StockCountView> BuildViewAsync(
        StockCount count,
        CancellationToken cancellationToken)
    {
        var lines = count.Lines.OrderBy(line => line.Id).ToArray();
        if (lines.Select(line => new StockPositionKey(line.ItemId, line.BatchNumber, line.ExpiryDate))
            .Distinct()
            .Count() != lines.Length)
        {
            throw new StockAvailabilityConflictException(
                "The stock-count snapshot has duplicate item/lot lines and cannot be read unambiguously.");
        }

        if (lines.Length == 0)
        {
            var movementDetected = await context.StockTransactions
                .AnyAsync(transaction => transaction.Id > count.MovementWatermark
                    && (transaction.FromLocationId == count.LocationId
                        || transaction.ToLocationId == count.LocationId),
                    cancellationToken);
            return new StockCountView(
                count.Id,
                count.LocationId,
                count.CompanyId,
                count.SnapshotAtUtc,
                movementDetected,
                []);
        }

        var itemIds = lines.Select(line => line.ItemId).Distinct().ToArray();
        var currentPositions = await context.StockInHand
            .AsNoTracking()
            .Where(stock => stock.LocationId == count.LocationId && itemIds.Contains(stock.ItemId))
            .Select(stock => new
            {
                stock.ItemId,
                stock.BatchNumber,
                stock.ExpiryDate,
                stock.Quantity
            })
            .ToListAsync(cancellationToken);
        var currentQuantities = new Dictionary<StockPositionKey, int>();
        foreach (var position in currentPositions)
        {
            if (!currentQuantities.TryAdd(
                    new StockPositionKey(position.ItemId, position.BatchNumber, position.ExpiryDate),
                    position.Quantity))
            {
                throw new StockAvailabilityConflictException(
                    "Multiple stock rows match the same item/lot bucket; reconcile inventory before reading a count.");
            }
        }

        var ownMovementIds = lines
            .Select(line => line.Variance?.StockTransactionId)
            .Where(transactionId => transactionId.HasValue)
            .Select(transactionId => transactionId!.Value)
            .ToArray();
        var movementsQuery = context.StockTransactions
            .AsNoTracking()
            .Where(transaction => transaction.Id > count.MovementWatermark
                && (transaction.FromLocationId == count.LocationId
                    || transaction.ToLocationId == count.LocationId));
        if (ownMovementIds.Length > 0)
            movementsQuery = movementsQuery.Where(transaction => !ownMovementIds.Contains(transaction.Id));
        var movements = await movementsQuery
            .Select(transaction => new
            {
                transaction.ItemId,
                transaction.BatchNumber,
                transaction.ExpiryDate
            })
            .ToListAsync(cancellationToken);
        var movedPositions = movements
            .Select(movement => new StockPositionKey(
                movement.ItemId,
                movement.BatchNumber,
                movement.ExpiryDate))
            .ToHashSet();

        var views = lines.Select(line =>
        {
            var key = new StockPositionKey(line.ItemId, line.BatchNumber, line.ExpiryDate);
            var observation = line.Observation;
            return ToView(
                line,
                currentQuantities.GetValueOrDefault(key),
                observation?.CountedQuantity,
            observation?.CreatedAt,
            observation?.CreatedBy,
            movedPositions.Contains(key),
            line.Variance is null
                ? null
                : new StockCountVarianceView(
                    line.Variance.ExpectedCurrentQuantity,
                    line.Variance.CountedQuantity,
                    line.Variance.DeltaQuantity,
                    line.Variance.Reason,
                    line.Variance.ApprovedUnitCost,
                    line.Variance.ValueAdjustment,
                    line.Variance.StockTransactionId,
                    line.Variance.CreatedAt,
                    line.Variance.CreatedBy));
        }).ToArray();

        return new StockCountView(
            count.Id,
            count.LocationId,
            count.CompanyId,
            count.SnapshotAtUtc,
            movements.Count > 0,
            views);
    }

    private static StockCountLineView ToView(
        StockCountLine line,
        int currentQuantity,
        int? countedQuantity,
        DateTime? countedAtUtc,
        string? countedBy,
        bool movementDetected,
        StockCountVarianceView? variance = null) => new(
            line.Id,
            line.ItemId,
            line.ItemCodeSnapshot,
            line.ItemDescriptionSnapshot,
            line.BatchNumber,
            line.ExpiryDate,
            line.SnapshotQuantity,
            currentQuantity,
            countedQuantity,
            countedAtUtc,
            countedBy,
            movementDetected,
            variance);

    private static int GetSignedQuantityDelta(StockTransaction transaction, int locationId) =>
        transaction.TransactionType switch
        {
            TransactionType.Quarantine or TransactionType.QuarantineRelease => 0,
            TransactionType.TransferDispatch => transaction.FromLocationId == locationId
                ? -transaction.Quantity
                : 0,
            TransactionType.TransferReceipt => transaction.ToLocationId == locationId
                ? transaction.Quantity
                : 0,
            TransactionType.TransferReturn => transaction.ToLocationId == locationId
                ? transaction.Quantity
                : 0,
            TransactionType.Transfer => transaction.FromLocationId == locationId
                ? -transaction.Quantity
                : transaction.ToLocationId == locationId
                    ? transaction.Quantity
                    : 0,
            TransactionType.CountAdjustment => transaction.ToLocationId == locationId
                ? transaction.Quantity
                : transaction.FromLocationId == locationId
                    ? -transaction.Quantity
                    : 0,
            _ => transaction.FromLocationId == locationId && transaction.ToLocationId == locationId
                ? transaction.Quantity
                : transaction.FromLocationId == locationId
                    ? -transaction.Quantity
                    : transaction.ToLocationId == locationId
                        ? transaction.Quantity
                        : 0
        };

    private static int GetValuationDirection(
        StockTransaction transaction,
        StockValuationEntry entry,
        int locationId) => entry.EntryType switch
    {
        StockValuationEntryType.Receipt
            or StockValuationEntryType.Return
            or StockValuationEntryType.TransferIn
            or StockValuationEntryType.TransferReturn => 1,
        StockValuationEntryType.Sale or StockValuationEntryType.TransferOut => -1,
        StockValuationEntryType.CountAdjustment => Math.Sign(GetSignedQuantityDelta(transaction, locationId)),
        _ => 0
    };

    private static decimal GetSignedValueDelta(
        StockTransaction transaction,
        StockValuationEntry entry,
        int locationId) => GetValuationDirection(transaction, entry, locationId) * entry.TotalValue;

    private readonly record struct StockPositionKey(
        int ItemId,
        string? BatchNumber,
        DateTime? ExpiryDate);
}
