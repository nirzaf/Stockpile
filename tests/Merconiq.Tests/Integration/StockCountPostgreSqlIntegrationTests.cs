using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Exceptions;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class StockCountPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
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

    private static StockTransaction CreateMovement(int itemId, int locationId, int quantity) => new()
    {
        ItemId = itemId,
        FromLocationId = locationId,
        ToLocationId = locationId,
        Quantity = quantity,
        TransactionType = TransactionType.Receive,
        TransactionDate = DateTime.UtcNow,
        BatchNumber = "COUNT-LOT"
    };
}
