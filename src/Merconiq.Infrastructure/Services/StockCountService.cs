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
    IUnitOfWork unitOfWork) : IStockCountService
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

            await EnsureAuthorizedLocationAsync(location, mutationScope);

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
                .SingleOrDefaultAsync(candidate => candidate.Id == countId, cancellationToken);
            if (count is null || count.Location.Branch?.CompanyId != count.CompanyId)
                return;

            await EnsureAuthorizedLocationAsync(count.Location, mutationScope);
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

    private async Task EnsureAuthorizedLocationAsync(
        Location location,
        StockMutationScope mutationScope)
    {
        if (location.Branch?.CompanyId != mutationScope.CompanyId)
        {
            throw new UnauthorizedAccessException(
                "Location company ownership changed; reauthorize the stock-count operation.");
        }

        if (mutationScope.Reauthorize is not null && !await mutationScope.Reauthorize())
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

        var movements = await context.StockTransactions
            .AsNoTracking()
            .Where(transaction => transaction.Id > count.MovementWatermark
                && (transaction.FromLocationId == count.LocationId
                    || transaction.ToLocationId == count.LocationId))
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
                movedPositions.Contains(key));
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
        bool movementDetected) => new(
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
            movementDetected);

    private readonly record struct StockPositionKey(
        int ItemId,
        string? BatchNumber,
        DateTime? ExpiryDate);
}
