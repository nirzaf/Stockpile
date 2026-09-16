using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

public sealed class OpeningStockApiTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public OpeningStockApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Preview_Admin_ReturnsRowValidationResultWithoutMutation()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = new Item
        {
            TenantId = "test-tenant",
            ExternalId = $"opening-{Guid.NewGuid():N}",
            ItemCode = $"OPEN-{Guid.NewGuid():N}"[..12],
            Description = "Opening item",
            IsActive = true
        };
        var location = new Location { TenantId = "test-tenant", Name = "Opening location" };
        db.Items.Add(item);
        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var client = _factory.CreateAuthenticatedClient("Admin");
        var response = await client.PostAsJsonAsync("/api/v1/stock/opening/preview", new
        {
            Csv = $"external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,{item.ExternalId},{location.Id},10,0"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await db.StockInHand.CountAsync(stock => stock.ItemId == item.Id)).Should().Be(0);
        (await db.StockTransactions.CountAsync(transaction => transaction.ItemId == item.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Preview_Viewer_IsForbidden()
    {
        var client = _factory.CreateAuthenticatedClient("Viewer");

        var response = await client.PostAsJsonAsync("/api/v1/stock/opening/preview", new
        {
            Csv = "external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,1,10,1"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Replay_Admin_AppliesOnce()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = new Item
        {
            ExternalId = $"replay-{Guid.NewGuid():N}",
            ItemCode = $"REP-{Guid.NewGuid():N}"[..12],
            Description = "Replay item",
            IsActive = true
        };
        var location = new Location { Name = "Replay location" };
        db.Items.Add(item);
        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var client = _factory.CreateAuthenticatedClient("Admin");
        var request = new
        {
            Csv = $"external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,{item.ExternalId},{location.Id},10,0",
            ImportReference = $"import-{Guid.NewGuid():N}",
            ApprovalReference = $"approval-{Guid.NewGuid():N}"
        };

        var first = await client.PostAsJsonAsync("/api/v1/stock/opening/replay", request);
        var second = await client.PostAsJsonAsync("/api/v1/stock/opening/replay", request);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await db.StockInHand.SingleAsync(stock => stock.ItemId == item.Id)).Quantity.Should().Be(10);
        (await db.OpeningStockImports.CountAsync(import => import.ImportReference == request.ImportReference)).Should().Be(1);
        (await db.OpeningStockImportLines.CountAsync(line => line.ItemId == item.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Replay_Viewer_IsForbidden()
    {
        var client = _factory.CreateAuthenticatedClient("Viewer");

        var response = await client.PostAsJsonAsync("/api/v1/stock/opening/replay", new
        {
            Csv = "external_reference,item_external_id,location_id,quantity,unit_cost\nopen-1,item-1,1,10,1",
            ImportReference = "import-1",
            ApprovalReference = "approval-1"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
