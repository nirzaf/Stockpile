using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Exceptions;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class StockCountPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Positive_unbatched_variance_is_costed_source_linked_immutable_and_idempotent()
    {
        fixture.EnsureEnabled();
        var tenantId = $"stock-count-variance-postgres-{Guid.NewGuid():N}";
        var (itemId, locationId) = await SeedPositionAsync(tenantId, 10, unitValue: 10m);
        var scope = new StockMutationScope(null, () => Task.FromResult(true));

        await using var operation = fixture.CreateContext(tenantId, "stock-count-variance-post");
        var unitOfWork = new UnitOfWork(operation);
        var countService = new StockCountService(
            operation,
            unitOfWork,
            CreateStockService(operation, tenantId, unitOfWork));
        var count = await countService.StartAsync(locationId, scope);
        var line = count.Lines.Should().ContainSingle().Subject;
        (await countService.RecordObservationAsync(count.Id, line.Id, 12, scope)).Should().NotBeNull();

        var missingCost = () => countService.PostVarianceAsync(
            count.Id, line.Id, "cycle count overage", approvedUnitCost: null, scope);
        await missingCost.Should().ThrowAsync<StockAvailabilityConflictException>()
            .WithMessage("A positive unbatched count variance requires an explicitly approved unit cost.");

        var posted = await countService.PostVarianceAsync(
            count.Id, line.Id, "cycle count overage", 15m, scope);
        posted.Should().NotBeNull();
        posted!.CurrentQuantity.Should().Be(12);
        posted.MovementDetected.Should().BeFalse();
        posted.Variance.Should().NotBeNull();
        posted.Variance!.DeltaQuantity.Should().Be(2);
        posted.Variance.ValueAdjustment.Should().Be(30m);
        posted.Variance.ApprovedUnitCost.Should().Be(15m);
        posted.Variance.Reason.Should().Be("cycle count overage");

        var reconciliation = await countService.GetReconciliationAsync(
            new StockCountReconciliationRequest(locationId, ItemId: itemId));
        reconciliation.Should().NotBeNull();
        var reconciledPosition = reconciliation!.Positions.Should().ContainSingle().Subject;
        reconciledPosition.OnHandQuantity.Should().Be(12);
        reconciledPosition.LedgerQuantity.Should().Be(12);
        reconciledPosition.QuantityDifference.Should().Be(0);
        reconciledPosition.ValuationTracked.Should().BeTrue();
        reconciledPosition.ValuationBucketQuantity.Should().Be(12);
        reconciledPosition.ValuationBucketValue.Should().Be(130m);
        reconciledPosition.ValuationLedgerQuantity.Should().Be(12);
        reconciledPosition.ValuationLedgerValue.Should().Be(130m);
        reconciledPosition.ValuationQuantityDifference.Should().Be(0);
        reconciledPosition.ValuationLedgerQuantityDifference.Should().Be(0);
        reconciledPosition.ValuationValueDifference.Should().Be(0m);
        reconciledPosition.Ledger.Should().HaveCount(2);

        var replay = await countService.PostVarianceAsync(
            count.Id, line.Id, "cycle count overage", 15m, scope);
        replay.Should().NotBeNull();
        replay!.Variance!.StockTransactionId.Should().Be(posted.Variance.StockTransactionId);
        (await operation.StockCountVariances.CountAsync()).Should().Be(1);
        (await operation.StockTransactions.CountAsync(transaction =>
            transaction.TransactionType == TransactionType.CountAdjustment)).Should().Be(1);

        var conflictingReplay = () => countService.PostVarianceAsync(
            count.Id, line.Id, "different reason", 15m, scope);
        await conflictingReplay.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A stock-count variance is immutable once posted; the replay payload does not match.");

        var stock = await operation.StockInHand.SingleAsync(candidate =>
            candidate.ItemId == itemId && candidate.LocationId == locationId);
        stock.Quantity.Should().Be(12);
        var bucket = await operation.StockValuationBuckets.SingleAsync(candidate =>
            candidate.ItemId == itemId && candidate.LocationId == locationId);
        bucket.Quantity.Should().Be(12);
        bucket.Value.Should().Be(130m);
        var valuation = await operation.StockValuationEntries.SingleAsync(entry =>
            entry.StockTransactionId == posted.Variance.StockTransactionId);
        valuation.EntryType.Should().Be(StockValuationEntryType.CountAdjustment);
        valuation.Quantity.Should().Be(2);
        valuation.UnitCost.Should().Be(15m);
        valuation.TotalValue.Should().Be(30m);

        await using var appendOnly = fixture.CreateContext(tenantId, "stock-count-variance-append-only");
        var mutationError = await Assert.ThrowsAsync<PostgresException>(async () =>
            await appendOnly.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"StockCountVariances\" SET \"Reason\" = 'edited' WHERE \"StockCountLineId\" = {line.Id} AND \"TenantId\" = {tenantId}"));
        mutationError.SqlState.Should().Be("55000");
    }

    [PostgreSqlFact]
    public async Task Positive_unbatched_variance_rejects_an_unvalued_existing_balance_atomically()
    {
        fixture.EnsureEnabled();
        var tenantId = $"stock-count-unvalued-variance-{Guid.NewGuid():N}";
        var (itemId, locationId) = await SeedPositionAsync(tenantId, 10);
        var scope = new StockMutationScope(null, () => Task.FromResult(true));

        await using var operation = fixture.CreateContext(tenantId, "stock-count-unvalued-variance");
        var unitOfWork = new UnitOfWork(operation);
        var service = new StockCountService(
            operation,
            unitOfWork,
            CreateStockService(operation, tenantId, unitOfWork));
        var count = await service.StartAsync(locationId, scope);
        var line = count.Lines.Should().ContainSingle().Subject;
        await service.RecordObservationAsync(count.Id, line.Id, 12, scope);

        var post = () => service.PostVarianceAsync(count.Id, line.Id, "cycle count overage", 15m, scope);
        await post.Should().ThrowAsync<StockAvailabilityConflictException>()
            .WithMessage("Unbatched stock must have a complete moving-average valuation before a positive count variance can be posted.");

        (await operation.StockInHand.SingleAsync(stock =>
            stock.ItemId == itemId && stock.LocationId == locationId)).Quantity.Should().Be(10);
        (await operation.StockValuationBuckets.CountAsync()).Should().Be(0);
        (await operation.StockValuationEntries.CountAsync()).Should().Be(0);
        (await operation.StockTransactions.CountAsync(transaction =>
            transaction.TransactionType == TransactionType.CountAdjustment)).Should().Be(0);
        (await operation.StockCountVariances.CountAsync()).Should().Be(0);
    }

    [PostgreSqlFact]
    public async Task Negative_unbatched_variance_uses_moving_average_and_preserves_value_balance()
    {
        fixture.EnsureEnabled();
        var tenantId = $"stock-count-negative-variance-{Guid.NewGuid():N}";
        var (itemId, locationId) = await SeedPositionAsync(tenantId, 10, unitValue: 10m);
        var scope = new StockMutationScope(null, () => Task.FromResult(true));

        await using var operation = fixture.CreateContext(tenantId, "stock-count-negative-variance");
        var unitOfWork = new UnitOfWork(operation);
        var service = new StockCountService(
            operation,
            unitOfWork,
            CreateStockService(operation, tenantId, unitOfWork));
        var count = await service.StartAsync(locationId, scope);
        var line = count.Lines.Should().ContainSingle().Subject;
        await service.RecordObservationAsync(count.Id, line.Id, 8, scope);

        var posted = await service.PostVarianceAsync(count.Id, line.Id, "cycle count shortage", null, scope);
        posted.Should().NotBeNull();
        posted!.CurrentQuantity.Should().Be(8);
        posted.Variance!.DeltaQuantity.Should().Be(-2);
        posted.Variance.ValueAdjustment.Should().Be(-20m);

        var stock = await operation.StockInHand.SingleAsync(candidate =>
            candidate.ItemId == itemId && candidate.LocationId == locationId);
        stock.Quantity.Should().Be(8);
        var bucket = await operation.StockValuationBuckets.SingleAsync(candidate =>
            candidate.ItemId == itemId && candidate.LocationId == locationId);
        bucket.Quantity.Should().Be(8);
        bucket.Value.Should().Be(80m);
        var entry = await operation.StockValuationEntries.SingleAsync(candidate =>
            candidate.StockTransactionId == posted.Variance.StockTransactionId);
        entry.EntryType.Should().Be(StockValuationEntryType.CountAdjustment);
        entry.UnitCost.Should().Be(10m);
        entry.TotalValue.Should().Be(20m);
    }

    [PostgreSqlFact]
    public async Task Count_variance_rejects_post_snapshot_movement_and_failed_approval_without_writes()
    {
        fixture.EnsureEnabled();
        var tenantId = $"stock-count-stale-variance-{Guid.NewGuid():N}";
        var (itemId, locationId) = await SeedPositionAsync(tenantId, 10, batchNumber: "COUNT-LOT");
        var allowed = new StockMutationScope(null, () => Task.FromResult(true));

        await using var operation = fixture.CreateContext(tenantId, "stock-count-stale-variance");
        var unitOfWork = new UnitOfWork(operation);
        var service = new StockCountService(
            operation,
            unitOfWork,
            CreateStockService(operation, tenantId, unitOfWork));
        var count = await service.StartAsync(locationId, allowed);
        var line = count.Lines.Should().ContainSingle().Subject;
        await service.RecordObservationAsync(count.Id, line.Id, 8, allowed);

        var denied = new StockMutationScope(null, () => Task.FromResult(false));
        var deniedPost = () => service.PostVarianceAsync(count.Id, line.Id, "approved shortage", null, denied);
        await deniedPost.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("The stock-count operation is not authorized.");

        await PostMovementAsync(fixture, tenantId, itemId, locationId, 1);
        var stalePost = () => service.PostVarianceAsync(count.Id, line.Id, "approved shortage", null, allowed);
        await stalePost.Should().ThrowAsync<StockAvailabilityConflictException>()
            .WithMessage("Stock changed after the physical count; take a new count before posting this variance.");

        (await operation.StockCountVariances.CountAsync()).Should().Be(0);
        (await operation.StockTransactions.CountAsync(transaction =>
            transaction.TransactionType == TransactionType.CountAdjustment)).Should().Be(0);
    }

    [PostgreSqlFact]
    public async Task Duplicate_nullable_lot_rows_block_a_stock_count_snapshot()
    {
        fixture.EnsureEnabled();
        var tenantId = $"stock-count-duplicate-postgres-{Guid.NewGuid():N}";
        int locationId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var item = new Item
            {
                ItemCode = $"COUNT-{Guid.NewGuid():N}",
                Description = "Duplicate stock-count item",
                ReorderLevel = 0
            };
            var location = new Location { Name = $"Count location {Guid.NewGuid():N}" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            setup.StockInHand.AddRange(
                new StockInHand { ItemId = item.Id, LocationId = location.Id, Quantity = 3 },
                new StockInHand { ItemId = item.Id, LocationId = location.Id, Quantity = 4 });
            await setup.SaveChangesAsync();
            locationId = location.Id;
        }

        await using var operation = fixture.CreateContext(tenantId, "stock-count-duplicate-check");
        var service = new StockCountService(operation, new UnitOfWork(operation));
        var start = () => service.StartAsync(
            locationId,
            new StockMutationScope(null, () => Task.FromResult(true)));

        await start.Should().ThrowAsync<StockAvailabilityConflictException>()
            .WithMessage("Multiple stock rows match the same item/lot bucket; reconcile inventory before starting a count.");
        await using var verification = fixture.CreateContext(tenantId);
        (await verification.StockCounts.CountAsync()).Should().Be(0);
    }

    [PostgreSqlFact]
    public async Task Snapshot_serializes_with_location_writes_and_flags_post_snapshot_movements()
    {
        fixture.EnsureEnabled();
        var tenantId = $"stock-count-postgres-{Guid.NewGuid():N}";
        int itemId;
        int locationId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var item = new Item
            {
                ItemCode = $"COUNT-{Guid.NewGuid():N}",
                Description = "PostgreSQL stock-count item",
                ReorderLevel = 0
            };
            var location = new Location { Name = $"Count location {Guid.NewGuid():N}" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();

            setup.StockInHand.Add(new StockInHand
            {
                ItemId = item.Id,
                LocationId = location.Id,
                BatchNumber = "COUNT-LOT",
                Quantity = 10
            });
            setup.StockTransactions.Add(CreateMovement(item.Id, location.Id, 10));
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
        }

        await using var inFlightContext = fixture.CreateContext(tenantId, "stock-count-lock-holder");
        var inFlightUnitOfWork = new UnitOfWork(inFlightContext);
        await inFlightUnitOfWork.BeginTransactionAsync();
        await inFlightUnitOfWork.AcquireLocationLocksAsync([locationId]);
        var inFlightStock = await inFlightContext.StockInHand.SingleAsync(stock =>
            stock.ItemId == itemId && stock.LocationId == locationId && stock.BatchNumber == "COUNT-LOT");
        inFlightStock.Quantity += 2;
        inFlightContext.StockTransactions.Add(CreateMovement(itemId, locationId, 2));
        await inFlightUnitOfWork.SaveChangesAsync();

        var snapshotTask = Task.Run(() => StartCountAsync(tenantId, locationId));
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        snapshotTask.IsCompleted.Should().BeFalse(
            "a count snapshot must wait until the location-locked posting commits");

        await inFlightUnitOfWork.CommitTransactionAsync();
        var snapshot = await snapshotTask.WaitAsync(TimeSpan.FromSeconds(15));
        snapshot.MovementDetected.Should().BeFalse();
        var line = snapshot.Lines.Should().ContainSingle().Subject;
        line.SnapshotQuantity.Should().Be(12);
        line.MovementDetected.Should().BeFalse();

        await PostMovementAsync(fixture, tenantId, itemId, locationId, 1);

        await using var verificationContext = fixture.CreateContext(tenantId);
        var current = await new StockCountService(
            verificationContext,
            new UnitOfWork(verificationContext)).GetAsync(snapshot.Id);

        current.Should().NotBeNull();
        current!.MovementDetected.Should().BeTrue();
        current.Lines.Should().ContainSingle().Which.CurrentQuantity.Should().Be(13);
        current.Lines.Single().MovementDetected.Should().BeTrue();

        await using var observationContext = fixture.CreateContext(tenantId, "stock-count-observation");
        var observationService = new StockCountService(
            observationContext,
            new UnitOfWork(observationContext));
        var observation = await observationService.RecordObservationAsync(
            snapshot.Id,
            line.Id,
            13,
            new StockMutationScope(null, () => Task.FromResult(true)));
        observation.Should().NotBeNull();
        observation!.CountedQuantity.Should().Be(13);
        observation.MovementDetected.Should().BeTrue();

        await using var appendOnlyContext = fixture.CreateContext(tenantId, "stock-count-append-only-check");
        var mutationError = await Assert.ThrowsAsync<PostgresException>(async () =>
            await appendOnlyContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"StockCountObservations\" SET \"CountedQuantity\" = 14 WHERE \"StockCountLineId\" = {line.Id} AND \"TenantId\" = {tenantId}"));
        mutationError.SqlState.Should().Be("55000");
    }

    private async Task<StockCountView> StartCountAsync(string tenantId, int locationId)
    {
        await using var context = fixture.CreateContext(tenantId, "stock-count-start");
        var service = new StockCountService(context, new UnitOfWork(context));
        return await service.StartAsync(
            locationId,
            new StockMutationScope(null, () => Task.FromResult(true)));
    }

    private static async Task PostMovementAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenantId,
        int itemId,
        int locationId,
        int quantity)
    {
        await using var context = fixture.CreateContext(tenantId, "stock-count-follow-up-movement");
        var unitOfWork = new UnitOfWork(context);
        await unitOfWork.BeginTransactionAsync();
        await unitOfWork.AcquireLocationLocksAsync([locationId]);
        var stock = await context.StockInHand.SingleAsync(candidate =>
            candidate.ItemId == itemId && candidate.LocationId == locationId && candidate.BatchNumber == "COUNT-LOT");
        stock.Quantity += quantity;
        context.StockTransactions.Add(CreateMovement(itemId, locationId, quantity));
        await unitOfWork.SaveChangesAsync();
        await unitOfWork.CommitTransactionAsync();
    }

    private static StockTransaction CreateMovement(
        int itemId,
        int locationId,
        int quantity,
        string? batchNumber = "COUNT-LOT") => new()
    {
        ItemId = itemId,
        FromLocationId = locationId,
        ToLocationId = locationId,
        Quantity = quantity,
        TransactionType = TransactionType.Receive,
        TransactionDate = DateTime.UtcNow,
        BatchNumber = batchNumber
    };

    private async Task<(int ItemId, int LocationId)> SeedPositionAsync(
        string tenantId,
        int quantity,
        string? batchNumber = null,
        decimal? unitValue = null)
    {
        await using var setup = fixture.CreateContext(tenantId);
        var item = new Item
        {
            ItemCode = $"COUNT-{Guid.NewGuid():N}",
            Description = "Count-adjustment integration item",
            ReorderLevel = 0
        };
        var location = new Location { Name = $"Count location {Guid.NewGuid():N}" };
        setup.Items.Add(item);
        setup.Locations.Add(location);
        await setup.SaveChangesAsync();

        var baselineMovement = CreateMovement(item.Id, location.Id, quantity, batchNumber);
        setup.StockInHand.Add(new StockInHand
        {
            ItemId = item.Id,
            LocationId = location.Id,
            Quantity = quantity,
            BatchNumber = batchNumber
        });
        setup.StockTransactions.Add(baselineMovement);
        if (unitValue is decimal value)
        {
            setup.StockValuationBuckets.Add(new StockValuationBucket
            {
                ItemId = item.Id,
                LocationId = location.Id,
                Quantity = quantity,
                Value = quantity * value
            });
            setup.StockValuationEntries.Add(new StockValuationEntry
            {
                StockTransaction = baselineMovement,
                ItemId = item.Id,
                LocationId = location.Id,
                EntryType = StockValuationEntryType.Receipt,
                Quantity = quantity,
                UnitCost = value,
                TotalValue = quantity * value
            });
        }

        await setup.SaveChangesAsync();
        return (item.Id, location.Id);
    }

    private static StockService CreateStockService(
        InventoryDbContext context,
        string tenantId,
        UnitOfWork unitOfWork) => new(
        new Repository<StockInHand>(context),
        new Repository<StockTransaction>(context),
        new Repository<Item>(context),
        new Repository<Location>(context),
        new Repository<Branch>(context),
        unitOfWork,
        new NoopWebhookDispatcher(),
        new TestTenantContext(tenantId),
        NullLogger<StockService>.Instance,
        new Repository<StockValuationBucket>(context),
        new Repository<StockValuationEntry>(context),
        new Repository<StockReservation>(context),
        new Repository<StockReservationAllocation>(context));

    private sealed class NoopWebhookDispatcher : IWebhookDispatcher
    {
        public Task EnqueueAsync<T>(WebhookEvent<T> webhookEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DispatchAsync<T>(WebhookEvent<T> webhookEvent) => Task.CompletedTask;
    }
}
