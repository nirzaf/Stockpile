using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

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

    private static OpeningStockImportService CreateService(InventoryDbContext context, string tenantId) =>
        new(context, new TestTenantContext(tenantId), new UnitOfWork(context), new HttpContextAccessor());
}
