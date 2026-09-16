using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Exceptions;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Merconiq.Tests.Integration;

public sealed class StockReservationIntegrationTests
{
    [Fact]
    public async Task Reservation_blocks_direct_sale_and_consumes_once()
    {
        var database = Guid.NewGuid().ToString();
        var tenant = "reservation-test";
        var (itemId, locationId) = await SeedAsync(database, tenant, 10);

        await using (var context = CreateContext(database, tenant))
        {
            await CreateService(context, tenant).CreateReservationAsync(
                new CreateStockReservationRequest(itemId, locationId, 6, "line-1"));
        }

        await using (var context = CreateContext(database, tenant))
        {
            var service = CreateService(context, tenant);
            var action = () => service.SellStockAsync(itemId, locationId, 5, "direct sale");
            await action.Should().ThrowAsync<StockAvailabilityConflictException>();
        }

        await using (var verify = CreateContext(database, tenant))
        {
            (await verify.StockInHand.SingleAsync()).Quantity.Should().Be(10);
            (await verify.StockInHand.SingleAsync()).ReservedQuantity.Should().Be(6);
            (await verify.StockTransactions.CountAsync()).Should().Be(0);
        }

        await using (var context = CreateContext(database, tenant))
        {
            await CreateService(context, tenant).ConsumeReservationAsync(
                new ConsumeStockReservationRequest("line-1", 4, "reservation sale"));
        }

        await using (var context = CreateContext(database, tenant))
        {
            var service = CreateService(context, tenant);
            var action = () => service.ConsumeReservationAsync(
                new ConsumeStockReservationRequest("line-1", 3, "too much"));
            await action.Should().ThrowAsync<StockAvailabilityConflictException>();
        }

        await using (var context = CreateContext(database, tenant))
        {
            await CreateService(context, tenant).ReleaseReservationAsync("line-1", "order cancelled");
        }

        await using (var verify = CreateContext(database, tenant))
        {
            var stock = await verify.StockInHand.SingleAsync();
            var reservation = await verify.StockReservations.SingleAsync();
            stock.Quantity.Should().Be(6);
            stock.ReservedQuantity.Should().Be(0);
            reservation.ConsumedQuantity.Should().Be(4);
            reservation.Status.Should().Be(StockReservationStatus.Released);
            (await verify.StockTransactions.CountAsync()).Should().Be(1);
        }

        await using (var context = CreateContext(database, tenant))
        {
            var result = (await CreateService(context, tenant).GetAvailabilityAsync(itemId, locationId)).Single();
            result.OnHand.Should().Be(6);
            result.Reserved.Should().Be(0);
            result.Available.Should().Be(6);
        }
    }

    [Fact]
    public async Task Reservation_without_lot_uses_earliest_expiry()
    {
        var database = Guid.NewGuid().ToString();
        var tenant = "fefo-test";
        int itemId;
        int locationId;
        await using (var context = CreateContext(database, tenant))
        {
            var item = new Item { ItemCode = $"FEFO-{Guid.NewGuid():N}", Description = "FEFO" };
            var location = new Location { Name = "FEFO location" };
            context.Items.Add(item);
            context.Locations.Add(location);
            await context.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
            context.StockInHand.AddRange(
                new StockInHand { ItemId = itemId, LocationId = locationId, Quantity = 2, BatchNumber = "LATE", ExpiryDate = new DateTime(2028, 1, 1) },
                new StockInHand { ItemId = itemId, LocationId = locationId, Quantity = 2, BatchNumber = "EARLY", ExpiryDate = new DateTime(2027, 1, 1) });
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext(database, tenant))
        {
            await CreateService(context, tenant).CreateReservationAsync(
                new CreateStockReservationRequest(itemId, locationId, 1, "line-fefo"));
        }

        await using var verify = CreateContext(database, tenant);
        var reservation = await verify.StockReservations.SingleAsync();
        reservation.BatchNumber.Should().Be("EARLY");
        reservation.ExpiryDate.Should().Be(new DateTime(2027, 1, 1));
    }

    private static async Task<(int ItemId, int LocationId)> SeedAsync(string database, string tenant, int quantity)
    {
        await using var context = CreateContext(database, tenant);
        var item = new Item { ItemCode = $"RES-{Guid.NewGuid():N}", Description = "Reservation" };
        var location = new Location { Name = "Reservation location" };
        context.Items.Add(item);
        context.Locations.Add(location);
        await context.SaveChangesAsync();
        context.StockInHand.Add(new StockInHand { ItemId = item.Id, LocationId = location.Id, Quantity = quantity });
        await context.SaveChangesAsync();
        return (item.Id, location.Id);
    }

    private static InventoryDbContext CreateContext(string database, string tenant) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(database)
            .Options, new TestTenantContext(tenant));

    private static StockService CreateService(InventoryDbContext context, string tenant) => new(
        new Repository<StockInHand>(context),
        new Repository<StockTransaction>(context),
        new Repository<Item>(context),
        new Repository<Location>(context),
        new Repository<Branch>(context),
        new UnitOfWork(context),
        new Mock<IWebhookDispatcher>().Object,
        new TestTenantContext(tenant),
        NullLogger<StockService>.Instance,
        reservationRepo: new Repository<StockReservation>(context));
}

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class StockReservationPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Concurrent_reservations_allow_only_available_quantity()
    {
        fixture.EnsureEnabled();
        var tenant = $"reservation-race-{Guid.NewGuid():N}";
        int itemId;
        int locationId;
        await using (var setup = fixture.CreateContext(tenant))
        {
            var item = new Item { ItemCode = $"RACE-{Guid.NewGuid():N}", Description = "Reservation race", ReorderLevel = 0 };
            var location = new Location { Name = "Reservation race location" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            setup.StockInHand.Add(new StockInHand { ItemId = item.Id, LocationId = location.Id, Quantity = 10 });
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
        }

        await using var firstContext = fixture.CreateContext(tenant, "reservation-first");
        await using var secondContext = fixture.CreateContext(tenant, "reservation-second");
        var first = CreateService(firstContext, tenant);
        var second = CreateService(secondContext, tenant);
        var results = await Task.WhenAll(
            CaptureAsync(() => first.CreateReservationAsync(new CreateStockReservationRequest(itemId, locationId, 6, "race-a"))),
            CaptureAsync(() => second.CreateReservationAsync(new CreateStockReservationRequest(itemId, locationId, 6, "race-b"))));

        results.Count(error => error is null).Should().Be(1);
        results.Count(error => error is StockAvailabilityConflictException).Should().Be(1);

        await using var verify = fixture.CreateContext(tenant);
        (await verify.StockReservations.CountAsync()).Should().Be(1);
        (await verify.StockInHand.SingleAsync()).ReservedQuantity.Should().Be(6);
    }

    [PostgreSqlFact]
    public async Task Expired_reservation_is_released_before_direct_sale()
    {
        fixture.EnsureEnabled();
        var tenant = $"reservation-expiry-{Guid.NewGuid():N}";
        int itemId;
        int locationId;
        await using (var setup = fixture.CreateContext(tenant))
        {
            var item = new Item { ItemCode = $"EXP-{Guid.NewGuid():N}", Description = "Reservation expiry", ReorderLevel = 0 };
            var location = new Location { Name = "Reservation expiry location" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            setup.StockInHand.Add(new StockInHand { ItemId = item.Id, LocationId = location.Id, Quantity = 10, ReservedQuantity = 4 });
            setup.StockReservations.Add(new StockReservation
            {
                ItemId = item.Id,
                LocationId = location.Id,
                SourceLineReference = "expired-line",
                Quantity = 4,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                Status = StockReservationStatus.Active
            });
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
        }

        await using (var context = fixture.CreateContext(tenant))
        {
            await CreateService(context, tenant).SellStockAsync(itemId, locationId, 4, "sale after expiry");
        }

        await using var verify = fixture.CreateContext(tenant);
        var stock = await verify.StockInHand.SingleAsync();
        var reservation = await verify.StockReservations.SingleAsync();
        stock.Quantity.Should().Be(6);
        stock.ReservedQuantity.Should().Be(0);
        reservation.Status.Should().Be(StockReservationStatus.Expired);
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static StockService CreateService(InventoryDbContext context, string tenant) => new(
        new Repository<StockInHand>(context),
        new Repository<StockTransaction>(context),
        new Repository<Item>(context),
        new Repository<Location>(context),
        new Repository<Branch>(context),
        new UnitOfWork(context),
        new Mock<IWebhookDispatcher>().Object,
        new TestTenantContext(tenant),
        NullLogger<StockService>.Instance,
        new Repository<StockValuationBucket>(context),
        new Repository<StockValuationEntry>(context),
        new Repository<StockReservation>(context));
}

public sealed class StockReservationApiTests(CustomWebApplicationFactory factory)
    : IClassFixture<CustomWebApplicationFactory>
{
    [Fact]
    public async Task Reservation_api_reports_availability_and_blocks_direct_sale()
    {
        var (item, location) = await SeedAsync();
        using var client = factory.CreateAuthenticatedClient();

        var create = await client.PostAsJsonAsync("/api/v1/stock/reservations", new
        {
            itemId = item.Id,
            locationId = location.Id,
            quantity = 5,
            sourceLineReference = $"api-line-{Guid.NewGuid():N}"
        });
        create.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var availability = await client.GetFromJsonAsync<ApiResponse<List<StockAvailabilityView>>>(
            $"/api/v1/stock/availability?itemId={item.Id}&locationId={location.Id}");
        availability!.Data.Should().ContainSingle(row =>
            row.OnHand == 10 && row.Reserved == 5 && row.Available == 5);

        var sale = await client.PostAsJsonAsync("/api/v1/stock/sell", new
        {
            itemId = item.Id,
            locationId = location.Id,
            quantity = 6,
            notes = "blocked direct sale"
        });
        sale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await sale.Content.ReadFromJsonAsync<ProblemDetailsResponse>();
        problem!.Detail.Should().Contain("reserved stock");
    }

    [Fact]
    public async Task Reservation_api_idempotency_key_replays_without_double_reserving()
    {
        var (item, location) = await SeedAsync();
        using var client = factory.CreateAuthenticatedClient();
        var key = $"reservation-{Guid.NewGuid():N}";
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var request = new
        {
            itemId = item.Id,
            locationId = location.Id,
            quantity = 5,
            sourceLineReference = $"api-idempotent-{Guid.NewGuid():N}"
        };

        var first = await client.PostAsJsonAsync("/api/v1/stock/reservations", request);
        var second = await client.PostAsJsonAsync("/api/v1/stock/reservations", request);

        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        (await db.StockReservations.CountAsync(reservation =>
            reservation.SourceLineReference == request.sourceLineReference)).Should().Be(1);
        (await db.StockInHand.SingleAsync(stock => stock.ItemId == item.Id && stock.LocationId == location.Id))
            .ReservedQuantity.Should().Be(5);
    }

    private async Task<(Item Item, Location Location)> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var company = new Company { Code = $"RC-{Guid.NewGuid():N}"[..10], LegalName = "Reservation company" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var branch = new Branch { CompanyId = company.Id, Code = $"RB-{Guid.NewGuid():N}"[..10], Name = "Reservation branch" };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        var item = new Item { ItemCode = $"RA-{Guid.NewGuid():N}"[..15], Description = "Reservation API" };
        var location = new Location { Name = "Reservation API location", BranchId = branch.Id };
        db.Items.Add(item);
        db.Locations.Add(location);
        await db.SaveChangesAsync();
        db.StockInHand.Add(new StockInHand { ItemId = item.Id, LocationId = location.Id, Quantity = 10 });
        await db.SaveChangesAsync();
        return (item, location);
    }

    private sealed record ProblemDetailsResponse(string? Title, string? Detail, int? Status);
}
