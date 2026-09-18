using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Exceptions;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Infrastructure;

public sealed class StockCountServiceTests
{
    [Fact]
    public async Task Snapshot_tracks_lot_movements_and_records_one_immutable_observation()
    {
        var tenantId = $"stock-count-{Guid.NewGuid():N}";
        var databaseName = Guid.NewGuid().ToString("N");
        await using var context = CreateContext(databaseName, tenantId);

        var countedItem = new Item
        {
            ItemCode = $"COUNT-{Guid.NewGuid():N}",
            Description = "Counted item",
            ReorderLevel = 0
        };
        var unrelatedItem = new Item
        {
            ItemCode = $"COUNT-{Guid.NewGuid():N}",
            Description = "Unrelated item",
            ReorderLevel = 0
        };
        var location = new Location { Name = $"Count location {Guid.NewGuid():N}" };
        context.Items.AddRange(countedItem, unrelatedItem);
        context.Locations.Add(location);
        await context.SaveChangesAsync();

        context.StockInHand.AddRange(
            new StockInHand
            {
                ItemId = countedItem.Id,
                LocationId = location.Id,
                BatchNumber = "LOT-A",
                Quantity = 6
            },
            new StockInHand
            {
                ItemId = countedItem.Id,
                LocationId = location.Id,
                BatchNumber = "LOT-B",
                Quantity = 4
            },
            new StockInHand
            {
                ItemId = unrelatedItem.Id,
                LocationId = location.Id,
                Quantity = 2
            });
        context.StockTransactions.AddRange(
            CreateMovement(countedItem.Id, location.Id, 6, "LOT-A"),
            CreateMovement(countedItem.Id, location.Id, 4, "LOT-B"),
            CreateMovement(unrelatedItem.Id, location.Id, 2, null));
        await context.SaveChangesAsync();

        var service = new StockCountService(context, new UnitOfWork(context));
        var scope = new StockMutationScope(null, () => Task.FromResult(true));
        var snapshot = await service.StartAsync(location.Id, scope);

        snapshot.Lines.Should().HaveCount(3);
        snapshot.MovementDetected.Should().BeFalse();
        var lotA = snapshot.Lines.Single(line => line.ItemId == countedItem.Id && line.BatchNumber == "LOT-A");
        lotA.SnapshotQuantity.Should().Be(6);
        lotA.MovementDetected.Should().BeFalse();

        var lotAStock = await context.StockInHand.SingleAsync(stock =>
            stock.ItemId == countedItem.Id && stock.LocationId == location.Id && stock.BatchNumber == "LOT-A");
        lotAStock.Quantity = 7;
        context.StockTransactions.Add(CreateMovement(countedItem.Id, location.Id, 1, "LOT-A"));
        await context.SaveChangesAsync();

        var current = await service.GetAsync(snapshot.Id);
        current.Should().NotBeNull();
        current!.MovementDetected.Should().BeTrue();
        var currentLotA = current.Lines.Single(line => line.Id == lotA.Id);
        currentLotA.CurrentQuantity.Should().Be(7);
        currentLotA.MovementDetected.Should().BeTrue();
        current.Lines.Single(line => line.ItemId == countedItem.Id && line.BatchNumber == "LOT-B")
            .MovementDetected.Should().BeFalse();
        current.Lines.Single(line => line.ItemId == unrelatedItem.Id)
            .MovementDetected.Should().BeFalse();

        var observation = await service.RecordObservationAsync(snapshot.Id, lotA.Id, 8, scope);
        observation.Should().NotBeNull();
        observation!.CountedQuantity.Should().Be(8);
        observation.MovementDetected.Should().BeTrue();

        var replay = await service.RecordObservationAsync(snapshot.Id, lotA.Id, 8, scope);
        replay.Should().Be(observation);

        var conflictingReplay = () => service.RecordObservationAsync(snapshot.Id, lotA.Id, 9, scope);
        await conflictingReplay.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A stock-count observation is immutable once recorded.");
        (await context.StockCountObservations.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Start_requires_fresh_authorization_and_count_reads_are_tenant_scoped()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var tenantA = $"stock-count-owner-{Guid.NewGuid():N}";
        var tenantB = $"stock-count-other-{Guid.NewGuid():N}";
        int locationId;
        int countId;

        await using (var ownerContext = CreateContext(databaseName, tenantA))
        {
            var location = new Location { Name = $"Count location {Guid.NewGuid():N}" };
            ownerContext.Locations.Add(location);
            await ownerContext.SaveChangesAsync();
            locationId = location.Id;

            var service = new StockCountService(ownerContext, new UnitOfWork(ownerContext));
            var deniedScope = new StockMutationScope(null, () => Task.FromResult(false));
            var denied = () => service.StartAsync(locationId, deniedScope);

            await denied.Should().ThrowAsync<UnauthorizedAccessException>();
            (await ownerContext.StockCounts.CountAsync()).Should().Be(0);

            var allowedScope = new StockMutationScope(null, () => Task.FromResult(true));
            countId = (await service.StartAsync(locationId, allowedScope)).Id;
        }

        await using var otherTenantContext = CreateContext(databaseName, tenantB);
        var otherTenantService = new StockCountService(
            otherTenantContext,
            new UnitOfWork(otherTenantContext));

        (await otherTenantService.GetAsync(countId)).Should().BeNull();
        (await otherTenantService.GetAuthorizationContextAsync(countId)).Should().BeNull();
    }

    [Fact]
    public async Task Movement_for_an_item_absent_from_the_snapshot_sets_only_the_count_level_flag()
    {
        var tenantId = $"stock-count-new-item-{Guid.NewGuid():N}";
        await using var context = CreateContext(Guid.NewGuid().ToString("N"), tenantId);
        var countedItem = new Item
        {
            ItemCode = $"COUNT-{Guid.NewGuid():N}",
            Description = "Counted item",
            ReorderLevel = 0
        };
        var location = new Location { Name = $"Count location {Guid.NewGuid():N}" };
        context.Items.Add(countedItem);
        context.Locations.Add(location);
        await context.SaveChangesAsync();

        context.StockInHand.Add(new StockInHand
        {
            ItemId = countedItem.Id,
            LocationId = location.Id,
            BatchNumber = "LOT-A",
            Quantity = 5
        });
        context.StockTransactions.Add(CreateMovement(countedItem.Id, location.Id, 5, "LOT-A"));
        await context.SaveChangesAsync();

        var service = new StockCountService(context, new UnitOfWork(context));
        var snapshot = await service.StartAsync(
            location.Id,
            new StockMutationScope(null, () => Task.FromResult(true)));
        snapshot.Lines.Should().ContainSingle();

        var lateItem = new Item
        {
            ItemCode = $"COUNT-{Guid.NewGuid():N}",
            Description = "Item introduced after the snapshot",
            ReorderLevel = 0
        };
        context.Items.Add(lateItem);
        await context.SaveChangesAsync();
        context.StockInHand.Add(new StockInHand
        {
            ItemId = lateItem.Id,
            LocationId = location.Id,
            BatchNumber = "LOT-NEW",
            Quantity = 1
        });
        context.StockTransactions.Add(CreateMovement(lateItem.Id, location.Id, 1, "LOT-NEW"));
        await context.SaveChangesAsync();

        var current = await service.GetAsync(snapshot.Id);

        current.Should().NotBeNull();
        current!.MovementDetected.Should().BeTrue();
        current.Lines.Should().ContainSingle().Which.MovementDetected.Should().BeFalse();
    }

    [Fact]
    public async Task Reconciliation_filters_by_item_and_lot_and_compares_quantity_and_valuation_ledgers()
    {
        var tenantId = $"stock-reconciliation-{Guid.NewGuid():N}";
        await using var context = CreateContext(Guid.NewGuid().ToString("N"), tenantId);
        var valuedItem = new Item
        {
            ItemCode = $"RECON-{Guid.NewGuid():N}",
            Description = "Valued reconciliation item",
            ReorderLevel = 0
        };
        var lotItem = new Item
        {
            ItemCode = $"RECON-{Guid.NewGuid():N}",
            Description = "Lot reconciliation item",
            ReorderLevel = 0
        };
        var location = new Location { Name = $"Reconciliation location {Guid.NewGuid():N}" };
        var transitDestination = new Location { Name = $"Transit destination {Guid.NewGuid():N}" };
        context.Items.AddRange(valuedItem, lotItem);
        context.Locations.AddRange(location, transitDestination);
        await context.SaveChangesAsync();

        var valuedMovement = CreateMovement(valuedItem.Id, location.Id, 10, null);
        context.StockInHand.Add(new StockInHand
        {
            ItemId = valuedItem.Id,
            LocationId = location.Id,
            Quantity = 10
        });
        context.StockTransactions.Add(valuedMovement);
        context.StockValuationBuckets.Add(new StockValuationBucket
        {
            ItemId = valuedItem.Id,
            LocationId = location.Id,
            Quantity = 10,
            Value = 100m
        });
        context.StockValuationEntries.Add(new StockValuationEntry
        {
            StockTransaction = valuedMovement,
            ItemId = valuedItem.Id,
            LocationId = location.Id,
            EntryType = StockValuationEntryType.Receipt,
            Quantity = 10,
            UnitCost = 10m,
            TotalValue = 100m
        });
        context.StockTransactions.Add(new StockTransaction
        {
            ItemId = valuedItem.Id,
            FromLocationId = location.Id,
            ToLocationId = transitDestination.Id,
            Quantity = 2,
            TransactionType = TransactionType.TransferReturn,
            TransactionDate = DateTime.UtcNow
        });
        context.StockInHand.Add(new StockInHand
        {
            ItemId = lotItem.Id,
            LocationId = location.Id,
            Quantity = 4,
            BatchNumber = "LOT-B"
        });
        context.StockTransactions.Add(CreateMovement(lotItem.Id, location.Id, 4, "LOT-B"));
        await context.SaveChangesAsync();

        var service = new StockCountService(context, new UnitOfWork(context));
        var valuedReport = await service.GetReconciliationAsync(
            new StockCountReconciliationRequest(location.Id, ItemId: valuedItem.Id));
        valuedReport.Should().NotBeNull();
        var valuedPosition = valuedReport!.Positions.Should().ContainSingle().Subject;
        valuedPosition.LedgerQuantity.Should().Be(10);
        valuedPosition.QuantityDifference.Should().Be(0);
        valuedPosition.ValuationTracked.Should().BeTrue();
        valuedPosition.ValuationBucketValue.Should().Be(100m);
        valuedPosition.ValuationLedgerValue.Should().Be(100m);
        valuedPosition.ValuationQuantityDifference.Should().Be(0);
        valuedPosition.ValuationLedgerQuantityDifference.Should().Be(0);
        valuedPosition.ValuationValueDifference.Should().Be(0m);
        valuedPosition.Ledger.Should().HaveCount(2);

        var lotReport = await service.GetReconciliationAsync(
            new StockCountReconciliationRequest(location.Id, BatchNumber: "LOT-B"));
        lotReport.Should().NotBeNull();
        lotReport!.Positions.Should().ContainSingle();
        lotReport.Positions[0].ItemId.Should().Be(lotItem.Id);
        lotReport.Positions[0].LedgerQuantity.Should().Be(4);
        lotReport.Positions[0].ValuationTracked.Should().BeFalse();

        (await service.GetReconciliationAsync(
            new StockCountReconciliationRequest(location.Id), [42])).Should().BeNull();
    }

    [Fact]
    public async Task Start_rejects_ambiguous_duplicate_item_lot_buckets_without_persisting_a_count()
    {
        var tenantId = $"stock-count-duplicate-{Guid.NewGuid():N}";
        await using var context = CreateContext(Guid.NewGuid().ToString("N"), tenantId);
        var item = new Item
        {
            ItemCode = $"COUNT-{Guid.NewGuid():N}",
            Description = "Duplicate stock row item",
            ReorderLevel = 0
        };
        var location = new Location { Name = $"Count location {Guid.NewGuid():N}" };
        context.Items.Add(item);
        context.Locations.Add(location);
        await context.SaveChangesAsync();
        context.StockInHand.AddRange(
            new StockInHand { ItemId = item.Id, LocationId = location.Id, Quantity = 3 },
            new StockInHand { ItemId = item.Id, LocationId = location.Id, Quantity = 4 });
        await context.SaveChangesAsync();

        var service = new StockCountService(context, new UnitOfWork(context));
        var start = () => service.StartAsync(
            location.Id,
            new StockMutationScope(null, () => Task.FromResult(true)));

        await start.Should().ThrowAsync<StockAvailabilityConflictException>()
            .WithMessage("Multiple stock rows match the same item/lot bucket; reconcile inventory before starting a count.");
        (await context.StockCounts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Read_returns_a_stock_conflict_if_a_duplicate_bucket_appears_after_the_snapshot()
    {
        var tenantId = $"stock-count-late-duplicate-{Guid.NewGuid():N}";
        await using var context = CreateContext(Guid.NewGuid().ToString("N"), tenantId);
        var item = new Item
        {
            ItemCode = $"COUNT-{Guid.NewGuid():N}",
            Description = "Duplicate stock row item",
            ReorderLevel = 0
        };
        var location = new Location { Name = $"Count location {Guid.NewGuid():N}" };
        context.Items.Add(item);
        context.Locations.Add(location);
        await context.SaveChangesAsync();
        context.StockInHand.Add(new StockInHand { ItemId = item.Id, LocationId = location.Id, Quantity = 3 });
        await context.SaveChangesAsync();

        var service = new StockCountService(context, new UnitOfWork(context));
        var snapshot = await service.StartAsync(
            location.Id,
            new StockMutationScope(null, () => Task.FromResult(true)));
        context.StockInHand.Add(new StockInHand { ItemId = item.Id, LocationId = location.Id, Quantity = 4 });
        await context.SaveChangesAsync();

        var read = () => service.GetAsync(snapshot.Id);
        await read.Should().ThrowAsync<StockAvailabilityConflictException>()
            .WithMessage("Multiple stock rows match the same item/lot bucket; reconcile inventory before reading a count.");
    }

    private static StockTransaction CreateMovement(int itemId, int locationId, int quantity, string? batchNumber) => new()
    {
        ItemId = itemId,
        FromLocationId = locationId,
        ToLocationId = locationId,
        Quantity = quantity,
        TransactionType = TransactionType.Receive,
        TransactionDate = DateTime.UtcNow,
        BatchNumber = batchNumber
    };

    private static InventoryDbContext CreateContext(string databaseName, string tenantId) => new(
        new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options,
        new TestTenantContext(tenantId));
}
