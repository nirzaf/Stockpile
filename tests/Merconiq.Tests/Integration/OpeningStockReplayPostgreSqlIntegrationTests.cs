using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class OpeningStockReplayPostgreSqlIntegrationTests
{
    private readonly PostgreSqlIntegrationFixture _fixture;

    public OpeningStockReplayPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [PostgreSqlFact]
    public async Task Concurrent_replays_apply_once_and_keep_stock_and_lineage_consistent()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"opening-replay-{Guid.NewGuid():N}";
        int itemId;
        int locationId;

        await using (var setup = _fixture.CreateContext(tenantId))
        {
            var item = new Item
            {
                ExternalId = "item-1",
                ItemCode = "OPEN-1",
                Description = "Opening item",
                IsActive = true
            };
            var location = new Location { Name = "Opening location" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
        }

        var request = new OpeningStockReplayRequest(
            $"external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,{locationId},10,0",
            "import-1",
            "approval-1");
        await using var firstContext = _fixture.CreateContext(tenantId, "opening-replay-first");
        await using var secondContext = _fixture.CreateContext(tenantId, "opening-replay-second");
        var firstService = CreateService(firstContext, tenantId);
        var secondService = CreateService(secondContext, tenantId);

        var results = await Task.WhenAll(
            firstService.ReplayAsync(request),
            secondService.ReplayAsync(request));

        results.Count(result => !result.AlreadyApplied).Should().Be(1);
        results.Count(result => result.AlreadyApplied).Should().Be(1);
        results.Single(result => !result.AlreadyApplied).AppliedRows.Should().Be(1);

        await using var verify = _fixture.CreateContext(tenantId);
        (await verify.StockInHand.SingleAsync(stock => stock.ItemId == itemId && stock.LocationId == locationId))
            .Quantity.Should().Be(10);
        (await verify.OpeningStockImports.CountAsync()).Should().Be(1);
        (await verify.OpeningStockImportLines.CountAsync()).Should().Be(1);
    }

    [PostgreSqlFact]
    public async Task Invalid_replay_is_rejected_without_persisting_an_import()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"opening-invalid-{Guid.NewGuid():N}";
        var locationId = 0;
        await using (var setup = _fixture.CreateContext(tenantId))
        {
            setup.Items.Add(new Item
            {
                ExternalId = "item-1",
                ItemCode = "OPEN-1",
                Description = "Opening item",
                IsActive = true
            });
            var location = new Location { Name = "Opening location" };
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            locationId = location.Id;
        }

        await using var context = _fixture.CreateContext(tenantId);
        var result = await CreateService(context, tenantId).ReplayAsync(new(
            $"external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,{locationId},10,",
            "import-invalid",
            "approval-invalid"));

        result.Rejected.Should().Be(1);
        (await context.StockInHand.CountAsync()).Should().Be(0);
        (await context.OpeningStockImports.CountAsync()).Should().Be(0);
    }

    [PostgreSqlFact]
    public async Task Approved_baseline_can_be_reversed_once_with_forward_stock_effects()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"opening-reversal-{Guid.NewGuid():N}";
        int itemId;
        int locationId;
        await using (var setup = _fixture.CreateContext(tenantId))
        {
            var item = new Item { ExternalId = "item-1", ItemCode = "OPEN-1", Description = "Opening item", IsActive = true };
            var location = new Location { Name = "Opening location" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
        }

        await using (var importContext = _fixture.CreateContext(tenantId))
        {
            await CreateOpeningService(importContext, tenantId).ReplayAsync(new(
                $"external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,{locationId},10,12.5",
                "import-1",
                "approval-1"));
        }

        await using var context = _fixture.CreateContext(tenantId);
        var unitOfWork = new UnitOfWork(context);
        var service = CreateOpeningService(context, tenantId, CreateStockService(context, tenantId, unitOfWork), unitOfWork);
        var request = new OpeningStockReversalRequest("import-1", "correction-1", "approval-2", "Wrong opening count");
        (await service.ReverseAsync(request)).AlreadyApplied.Should().BeFalse();
        (await service.ReverseAsync(request)).AlreadyApplied.Should().BeTrue();

        (await context.StockInHand.SingleAsync(stock => stock.ItemId == itemId && stock.LocationId == locationId)).Quantity.Should().Be(0);
        var bucket = await context.StockValuationBuckets.SingleAsync();
        bucket.Quantity.Should().Be(0);
        bucket.Value.Should().Be(0);
        (await context.StockTransactions.CountAsync()).Should().Be(2);
        (await context.StockTransactions.SingleAsync(transaction => transaction.TransactionType == TransactionType.Sell))
            .Notes.Should().Contain("correction-1");
        (await context.OpeningStockCorrections.CountAsync()).Should().Be(1);
    }

    [PostgreSqlFact]
    public async Task Concurrent_identical_reversals_apply_once_and_return_the_replay_result()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"opening-reversal-race-{Guid.NewGuid():N}";
        int itemId;
        int firstLocationId;
        int secondLocationId;
        await using (var setup = _fixture.CreateContext(tenantId))
        {
            setup.Items.Add(new Item { ExternalId = "item-1", ItemCode = "OPEN-1", Description = "Opening item", IsActive = true });
            setup.Locations.AddRange(
                new Location { Name = "Opening location 1" },
                new Location { Name = "Opening location 2" });
            await setup.SaveChangesAsync();
            itemId = await setup.Items.Select(item => item.Id).SingleAsync();
            var locations = await setup.Locations.OrderBy(location => location.Id).ToArrayAsync();
            firstLocationId = locations[0].Id;
            secondLocationId = locations[1].Id;
        }

        var csv = $"external_reference,item_external_id,location_id,quantity,unit_cost\n" +
                  $"open-1,item-1,{secondLocationId},4,12.5\n" +
                  $"open-2,item-1,{firstLocationId},6,12.5";
        await using (var importContext = _fixture.CreateContext(tenantId))
        {
            await CreateOpeningService(importContext, tenantId).ReplayAsync(new(
                csv, "import-race", "approval-1"));
        }

        await using var firstContext = _fixture.CreateContext(tenantId, "opening-reversal-first");
        await using var secondContext = _fixture.CreateContext(tenantId, "opening-reversal-second");
        var firstUnitOfWork = new UnitOfWork(firstContext);
        var secondUnitOfWork = new UnitOfWork(secondContext);
        var firstService = CreateOpeningService(firstContext, tenantId,
            CreateStockService(firstContext, tenantId, firstUnitOfWork), firstUnitOfWork);
        var secondService = CreateOpeningService(secondContext, tenantId,
            CreateStockService(secondContext, tenantId, secondUnitOfWork), secondUnitOfWork);
        var request = new OpeningStockReversalRequest(
            "import-race", "correction-race", "approval-2", "Correct opening quantities");

        var results = await Task.WhenAll(
            firstService.ReverseAsync(request),
            secondService.ReverseAsync(request));

        results.Count(result => !result.AlreadyApplied).Should().Be(1);
        results.Count(result => result.AlreadyApplied).Should().Be(1);
        await using var verify = _fixture.CreateContext(tenantId);
        (await verify.StockInHand.Where(stock => stock.ItemId == itemId).SumAsync(stock => stock.Quantity))
            .Should().Be(0);
        (await verify.StockTransactions.CountAsync(transaction => transaction.TransactionType == TransactionType.Sell))
            .Should().Be(2);
        (await verify.OpeningStockCorrections.CountAsync()).Should().Be(1);
    }

    [PostgreSqlFact]
    public async Task Approved_baseline_and_correction_rows_are_database_append_only()
    {
        _fixture.EnsureEnabled();
        var tenantId = $"opening-lock-{Guid.NewGuid():N}";
        int importId;
        await using (var setup = _fixture.CreateContext(tenantId))
        {
            setup.Items.Add(new Item { ExternalId = "item-1", ItemCode = "OPEN-1", Description = "Opening item", IsActive = true });
            setup.Locations.Add(new Location { Name = "Opening location" });
            await setup.SaveChangesAsync();
            var item = await setup.Items.SingleAsync();
            var location = await setup.Locations.SingleAsync();
            await CreateOpeningService(setup, tenantId).ReplayAsync(new(
                $"external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,{location.Id},10,12.5",
                "import-1",
                "approval-1"));
            importId = await setup.OpeningStockImports.Select(import => import.Id).SingleAsync();
            item.Id.Should().BeGreaterThan(0);
        }

        await using var context = _fixture.CreateContext(tenantId);
        await FluentActions.Invoking(() => context.Database.ExecuteSqlRawAsync(
                "UPDATE \"OpeningStockImports\" SET \"ApprovalReference\" = 'changed' WHERE \"Id\" = {0}", importId))
            .Should().ThrowAsync<PostgresException>();
        await FluentActions.Invoking(() => context.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"OpeningStockImports\" WHERE \"Id\" = {0}", importId))
            .Should().ThrowAsync<PostgresException>();
        await FluentActions.Invoking(() => context.Database.ExecuteSqlRawAsync("TRUNCATE \"OpeningStockImports\""))
            .Should().ThrowAsync<PostgresException>();
    }

    private static OpeningStockImportService CreateOpeningService(
        InventoryDbContext context,
        string tenantId,
        IStockService? stockService = null,
        IUnitOfWork? unitOfWork = null) =>
        new(context, new TestTenantContext(tenantId), unitOfWork ?? new UnitOfWork(context), new HttpContextAccessor(), stockService);

    private static StockService CreateStockService(InventoryDbContext context, string tenantId, IUnitOfWork unitOfWork) => new(
        new Repository<StockInHand>(context),
        new Repository<StockTransaction>(context),
        new Repository<Item>(context),
        new Repository<Location>(context),
        new Repository<Branch>(context),
        unitOfWork,
        new Mock<IWebhookDispatcher>().Object,
        new TestTenantContext(tenantId),
        NullLogger<StockService>.Instance,
        new Repository<StockValuationBucket>(context),
        new Repository<StockValuationEntry>(context));

    private static OpeningStockImportService CreateService(InventoryDbContext context, string tenantId) =>
        CreateOpeningService(context, tenantId);
}
