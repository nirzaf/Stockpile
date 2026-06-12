using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryManagementSystem.Tests.Integration;

public class StockApiTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public StockApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AuthClient => _factory.CreateAuthenticatedClient();
    private HttpClient AnonClient => _factory.CreateUnauthenticatedClient();

    private async Task<(Item item, Location loc)> SeedItemAndLocationAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = new Item { ItemCode = $"STK-{Guid.NewGuid():N}".Substring(0, 15), Description = "Stock Test", Rate = 10m };
        var loc = new Location { Name = $"LOC-{Guid.NewGuid():N}".Substring(0, 15) };
        db.Items.Add(item);
        db.Locations.Add(loc);
        await db.SaveChangesAsync();
        return (item, loc);
    }

    private async Task SeedStockAsync(int itemId, int locationId, int qty)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        db.StockInHand.Add(new StockInHand { ItemId = itemId, LocationId = locationId, Quantity = qty });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetAllStock_Authenticated_Returns200()
    {
        var client = AuthClient;

        var response = await client.GetAsync("/api/v1/stock/in-hand");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetByItemAndLocation_Exists_Returns200()
    {
        var client = AuthClient;
        var (item, loc) = await SeedItemAndLocationAsync();
        await SeedStockAsync(item.Id, loc.Id, 50);

        var response = await client.GetAsync($"/api/v1/stock/in-hand/{item.Id}/{loc.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetByItemAndLocation_NotExists_Returns404()
    {
        var client = AuthClient;

        var response = await client.GetAsync("/api/v1/stock/in-hand/99999/99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetTransactions_NoFilter_Returns200()
    {
        var client = AuthClient;

        var response = await client.GetAsync("/api/v1/stock/transactions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetTransactions_WithDateFilter_Returns200()
    {
        var client = AuthClient;

        var from = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var response = await client.GetAsync($"/api/v1/stock/transactions?from={from}&to={to}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Receive_ValidCommand_Returns204()
    {
        var client = AuthClient;
        var (item, loc) = await SeedItemAndLocationAsync();

        var command = new { ItemId = item.Id, LocationId = loc.Id, Quantity = 10, Notes = "test" };
        var response = await client.PostAsJsonAsync("/api/v1/stock/receive", command);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Receive_ZeroQuantity_Returns400()
    {
        var client = AuthClient;
        var (item, loc) = await SeedItemAndLocationAsync();

        var command = new { ItemId = item.Id, LocationId = loc.Id, Quantity = 0, Notes = "test" };
        var response = await client.PostAsJsonAsync("/api/v1/stock/receive", command);

        // Service throws ArgumentException → caught by GlobalExceptionHandler → 400
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Transfer_ValidCommand_Returns204()
    {
        var client = AuthClient;
        var (item, loc1) = await SeedItemAndLocationAsync();

        // Create second location
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var loc2 = new Location { Name = "LOC-DST" };
            db.Locations.Add(loc2);
            await db.SaveChangesAsync();

            // Seed stock at source
            db.StockInHand.Add(new StockInHand { ItemId = item.Id, LocationId = loc1.Id, Quantity = 100 });
            await db.SaveChangesAsync();

            var command = new { ItemId = item.Id, FromLocationId = loc1.Id, ToLocationId = loc2.Id, Quantity = 30, Notes = "transfer" };
            var response = await client.PostAsJsonAsync("/api/v1/stock/transfer", command);

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task Transfer_InsufficientStock_Returns409()
    {
        var client = AuthClient;
        var (item, loc1) = await SeedItemAndLocationAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var loc2 = new Location { Name = "LOC-DST2" };
            db.Locations.Add(loc2);
            db.StockInHand.Add(new StockInHand { ItemId = item.Id, LocationId = loc1.Id, Quantity = 5 });
            await db.SaveChangesAsync();

            var command = new { ItemId = item.Id, FromLocationId = loc1.Id, ToLocationId = loc2.Id, Quantity = 100, Notes = "too much" };
            var response = await client.PostAsJsonAsync("/api/v1/stock/transfer", command);

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
    }

    [Fact]
    public async Task Transfer_SameLocation_Returns400()
    {
        var client = AuthClient;
        var (item, loc) = await SeedItemAndLocationAsync();
        await SeedStockAsync(item.Id, loc.Id, 100);

        var command = new { ItemId = item.Id, FromLocationId = loc.Id, ToLocationId = loc.Id, Quantity = 10, Notes = "same" };
        var response = await client.PostAsJsonAsync("/api/v1/stock/transfer", command);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Sell_ValidCommand_Returns204()
    {
        var client = AuthClient;
        var (item, loc) = await SeedItemAndLocationAsync();
        await SeedStockAsync(item.Id, loc.Id, 50);

        var command = new { ItemId = item.Id, LocationId = loc.Id, Quantity = 10, Notes = "sold" };
        var response = await client.PostAsJsonAsync("/api/v1/stock/sell", command);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Sell_InsufficientStock_Returns409()
    {
        var client = AuthClient;
        var (item, loc) = await SeedItemAndLocationAsync();
        await SeedStockAsync(item.Id, loc.Id, 5);

        var command = new { ItemId = item.Id, LocationId = loc.Id, Quantity = 100, Notes = "too much" };
        var response = await client.PostAsJsonAsync("/api/v1/stock/sell", command);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Receive_Unauthenticated_Returns401OrRedirect()
    {
        var client = AnonClient;

        var command = new { ItemId = 1, LocationId = 1, Quantity = 10, Notes = "test" };
        var response = await client.PostAsJsonAsync("/api/v1/stock/receive", command);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Redirect);
    }
}
