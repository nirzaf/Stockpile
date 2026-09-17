using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

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
            result.Quarantined.Should().Be(0);
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

        var webhookDispatcher = new RecordingWebhookDispatcher();
        await using (var context = CreateContext(database, tenant))
        {
            await CreateService(context, tenant, webhookDispatcher).CreateReservationAsync(
                new CreateStockReservationRequest(itemId, locationId, 1, "line-fefo"));
        }

        await using var verify = CreateContext(database, tenant);
        var reservation = await verify.StockReservations.SingleAsync();
        reservation.BatchNumber.Should().Be("EARLY");
        reservation.ExpiryDate.Should().Be(new DateTime(2027, 1, 1));
        var payload = JsonSerializer.SerializeToElement(
            webhookDispatcher.Events.Single(item => item.EventType == "Stock.Reserved").Payload);
        payload.GetProperty("BatchNumber").GetString().Should().Be("EARLY");
        DateOnly.FromDateTime(payload.GetProperty("ExpiryDate").GetDateTime())
            .Should().Be(new DateOnly(2027, 1, 1));
        payload.GetProperty("ExpiryExceptionReason").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Ordinary_fefo_reservation_skips_expired_lots_when_fresh_stock_is_sufficient()
    {
        var database = Guid.NewGuid().ToString();
        const string tenant = "fefo-skips-expired-test";
        var expiredDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-2), DateTimeKind.Utc);
        var freshDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(20), DateTimeKind.Utc);
        int itemId;
        int locationId;

        await using (var setup = CreateContext(database, tenant))
        {
            var item = new Item { ItemCode = $"FEFO-SKIP-{Guid.NewGuid():N}", Description = "FEFO skips expired" };
            var location = new Location { Name = "FEFO skips expired location" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
            setup.StockInHand.AddRange(
                new StockInHand
                {
                    ItemId = itemId,
                    LocationId = locationId,
                    Quantity = 2,
                    BatchNumber = "EXPIRED-FEFO",
                    ExpiryDate = expiredDate
                },
                new StockInHand
                {
                    ItemId = itemId,
                    LocationId = locationId,
                    Quantity = 3,
                    BatchNumber = "FRESH-FEFO",
                    ExpiryDate = freshDate
                });
            await setup.SaveChangesAsync();
        }

        await using (var create = CreateContext(database, tenant))
        {
            await CreateService(create, tenant).CreateReservationAsync(
                new CreateStockReservationRequest(itemId, locationId, 3, "line-fefo-skips-expired"));
        }

        await using var verify = CreateContext(database, tenant);
        var reservation = await CreateService(verify, tenant).GetReservationAsync("line-fefo-skips-expired");
        reservation.Should().NotBeNull();
        reservation!.Allocations.Should().NotBeNull();
        reservation.Allocations!.Should().ContainSingle().Which.Should().Be(
            new StockReservationAllocationView("FRESH-FEFO", freshDate, 3, 0, 3, null));
        var stock = await verify.StockInHand
            .Where(row => row.ItemId == itemId && row.LocationId == locationId)
            .OrderBy(row => row.ExpiryDate)
            .ToListAsync();
        stock.Select(row => row.ReservedQuantity).Should().Equal(0, 3);
    }

    [Fact]
    public async Task Retrying_an_existing_unselected_reservation_does_not_reselect_a_later_expired_lot()
    {
        var database = Guid.NewGuid().ToString();
        var tenant = "reservation-idempotency-expiry-test";
        int itemId;
        int locationId;
        var requestedExpiry = DateTime.UtcNow.Date.AddDays(2);
        var expiredDate = DateTime.UtcNow.Date.AddDays(-1);

        await using (var context = CreateContext(database, tenant))
        {
            var item = new Item { ItemCode = $"IDEMPOTENT-FEFO-{Guid.NewGuid():N}", Description = "Idempotent FEFO" };
            var location = new Location { Name = $"Idempotent FEFO {Guid.NewGuid():N}" };
            context.Items.Add(item);
            context.Locations.Add(location);
            await context.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
            context.StockInHand.Add(new StockInHand
            {
                ItemId = itemId,
                LocationId = locationId,
                Quantity = 3,
                BatchNumber = "FUTURE",
                ExpiryDate = requestedExpiry
            });
            await context.SaveChangesAsync();
        }

        var request = new CreateStockReservationRequest(itemId, locationId, 1, "line-idempotent-fefo");
        await using (var context = CreateContext(database, tenant))
            await CreateService(context, tenant).CreateReservationAsync(request);

        await using (var context = CreateContext(database, tenant))
        {
            context.StockInHand.Add(new StockInHand
            {
                ItemId = itemId,
                LocationId = locationId,
                Quantity = 3,
                BatchNumber = "EXPIRED",
                ExpiryDate = expiredDate
            });
            await context.SaveChangesAsync();
        }

        await using (var context = CreateContext(database, tenant))
            await CreateService(context, tenant).CreateReservationAsync(request);

        await using var verify = CreateContext(database, tenant);
        var reservation = await verify.StockReservations.SingleAsync();
        reservation.BatchNumber.Should().Be("FUTURE");
        reservation.ExpiryDate.Should().Be(requestedExpiry);
        var reservedQuantities = await verify.StockInHand
            .OrderBy(stock => stock.BatchNumber)
            .Select(stock => stock.ReservedQuantity)
            .ToArrayAsync();
        reservedQuantities.Should().Equal(0, 1);
    }

    [Fact]
    public async Task Explicit_expired_lot_cannot_be_reserved_without_changing_stock_or_reservations()
    {
        var database = Guid.NewGuid().ToString();
        var tenant = "expired-reserve-test";
        var state = await SeedExpiredLotAsync(database, tenant, "existing-expired-reservation");

        await using (var context = CreateContext(database, tenant))
        {
            var action = () => CreateService(context, tenant).CreateReservationAsync(
                new CreateStockReservationRequest(state.ItemId, state.LocationId, 1, "new-line",
                    "LOT-EXPIRED", state.ExpiryDate));
            await action.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("An audit reason is required to use an expired stock lot.");
        }

        await AssertExpiredLotStateUnchangedAsync(database, tenant, state);
    }

    [Fact]
    public async Task Automatically_selected_expired_lot_is_unavailable_before_expired_reservations_are_released()
    {
        var database = Guid.NewGuid().ToString();
        var tenant = "expired-auto-reserve-test";
        var state = await SeedExpiredLotAsync(database, tenant, "existing-expired-reservation");

        await using (var context = CreateContext(database, tenant))
        {
            var action = () => CreateService(context, tenant).CreateReservationAsync(
                new CreateStockReservationRequest(state.ItemId, state.LocationId, 1, "new-line"));
            await action.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("No stock is available for the requested lot.");
        }

        await AssertExpiredLotStateUnchangedAsync(database, tenant, state);
    }

    [Fact]
    public async Task Sale_of_explicit_expired_lot_is_rejected_without_changing_stock_or_reservations()
    {
        var database = Guid.NewGuid().ToString();
        var tenant = "expired-sale-test";
        var state = await SeedExpiredLotAsync(database, tenant, "existing-expired-reservation");

        await using (var context = CreateContext(database, tenant))
        {
            var action = () => CreateService(context, tenant).SellStockAsync(
                state.ItemId, state.LocationId, 1, "expired lot sale", "LOT-EXPIRED", state.ExpiryDate);
            await action.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("An audit reason is required to use an expired stock lot.");
        }

        await AssertExpiredLotStateUnchangedAsync(database, tenant, state);
    }

    [Fact]
    public async Task Consuming_reservation_for_expired_lot_is_rejected_without_releasing_or_consuming_it()
    {
        var database = Guid.NewGuid().ToString();
        var tenant = "expired-consume-test";
        var state = await SeedExpiredLotAsync(database, tenant, "line-expired");

        await using (var context = CreateContext(database, tenant))
        {
            var action = () => CreateService(context, tenant).ConsumeReservationAsync(
                new ConsumeStockReservationRequest("line-expired", 1, "expired reservation sale"));
            await action.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("An audit reason is required to use an expired stock lot.");
        }

        await AssertExpiredLotStateUnchangedAsync(database, tenant, state);
    }

    [Fact]
    public async Task Transfer_of_explicit_expired_lot_is_rejected_without_changing_stock_or_reservations()
    {
        var database = Guid.NewGuid().ToString();
        var tenant = "expired-transfer-test";
        var state = await SeedExpiredLotAsync(database, tenant, "existing-expired-reservation");

        await using (var context = CreateContext(database, tenant))
        {
            var action = () => CreateService(context, tenant).TransferStockAsync(
                state.ItemId, state.LocationId, state.DestinationLocationId, 1,
                "expired lot transfer", "LOT-EXPIRED", state.ExpiryDate);
            await action.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("An audit reason is required to use an expired stock lot.");
        }

        await AssertExpiredLotStateUnchangedAsync(database, tenant, state);
    }

    [Fact]
    public async Task Lot_expiring_today_remains_eligible_for_reservation()
    {
        var database = Guid.NewGuid().ToString();
        var tenant = "expiry-today-test";
        var (itemId, locationId) = await SeedAsync(database, tenant, 5);
        var expiryDate = DateTime.UtcNow.Date;
        await using (var setup = CreateContext(database, tenant))
        {
            var stock = await setup.StockInHand.SingleAsync();
            stock.BatchNumber = "LOT-TODAY";
            stock.ExpiryDate = expiryDate;
            await setup.SaveChangesAsync();
        }

        await using (var context = CreateContext(database, tenant))
        {
            await CreateService(context, tenant).CreateReservationAsync(
                new CreateStockReservationRequest(itemId, locationId, 1, "line-today", "LOT-TODAY", expiryDate));
        }

        await using var verify = CreateContext(database, tenant);
        (await verify.StockInHand.SingleAsync()).ReservedQuantity.Should().Be(1);
        var reservation = await verify.StockReservations.SingleAsync();
        reservation.ExpiryDate.Should().Be(expiryDate);
        reservation.BatchNumber.Should().Be("LOT-TODAY");
    }

    private static async Task<ExpiredLotSeed> SeedExpiredLotAsync(
        string database,
        string tenant,
        string reservationReference)
    {
        await using var context = CreateContext(database, tenant);
        var company = new Company
        {
            Code = $"EXP-{Guid.NewGuid():N}",
            LegalName = "Expired lot test company",
            TenantId = tenant
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();

        var branch = new Branch
        {
            CompanyId = company.Id,
            Code = "TEST",
            Name = "Expired lot test branch",
            TenantId = tenant
        };
        context.Branches.Add(branch);
        await context.SaveChangesAsync();

        var source = new Location { Name = "Expired lot source", BranchId = branch.Id, TenantId = tenant };
        var destination = new Location { Name = "Expired lot destination", BranchId = branch.Id, TenantId = tenant };
        context.Locations.AddRange(source, destination);
        await context.SaveChangesAsync();

        var item = new Item
        {
            ItemCode = $"EXP-{Guid.NewGuid():N}",
            Description = "Expired lot test item",
            ReorderLevel = 0,
            TenantId = tenant
        };
        context.Items.Add(item);
        await context.SaveChangesAsync();

        var expiryDate = DateTime.SpecifyKind(
            DateTime.UtcNow.Date.AddDays(-1), DateTimeKind.Unspecified);
        context.StockInHand.Add(new StockInHand
        {
            ItemId = item.Id,
            LocationId = source.Id,
            Quantity = 10,
            ReservedQuantity = 2,
            BatchNumber = "LOT-EXPIRED",
            ExpiryDate = expiryDate,
            TenantId = tenant
        });
        context.StockReservations.Add(new StockReservation
        {
            ItemId = item.Id,
            LocationId = source.Id,
            SourceLineReference = reservationReference,
            BatchNumber = "LOT-EXPIRED",
            ExpiryDate = expiryDate,
            Quantity = 2,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Status = StockReservationStatus.Active,
            TenantId = tenant
        });
        await context.SaveChangesAsync();
        return new ExpiredLotSeed(item.Id, source.Id, destination.Id, expiryDate, reservationReference);
    }

    private static async Task AssertExpiredLotStateUnchangedAsync(
        string database,
        string tenant,
        ExpiredLotSeed state)
    {
        await using var verify = CreateContext(database, tenant);
        var stock = await verify.StockInHand.SingleAsync();
        stock.ItemId.Should().Be(state.ItemId);
        stock.LocationId.Should().Be(state.LocationId);
        stock.Quantity.Should().Be(10);
        stock.ReservedQuantity.Should().Be(2);
        stock.BatchNumber.Should().Be("LOT-EXPIRED");
        stock.ExpiryDate.Should().Be(state.ExpiryDate);
        stock.ExpiryDate!.Value.Kind.Should().Be(DateTimeKind.Utc);
        stock.ExpiryDate.Value.TimeOfDay.Should().Be(TimeSpan.Zero);

        var reservation = await verify.StockReservations.SingleAsync();
        reservation.SourceLineReference.Should().Be(state.ReservationReference);
        reservation.Status.Should().Be(StockReservationStatus.Active);
        reservation.Quantity.Should().Be(2);
        reservation.ConsumedQuantity.Should().Be(0);
        reservation.ExpiryDate!.Value.Kind.Should().Be(DateTimeKind.Utc);
        reservation.ExpiryDate.Value.TimeOfDay.Should().Be(TimeSpan.Zero);
        reservation.ExpiresAt.Should().BeBefore(DateTimeOffset.UtcNow);
        reservation.ClosedAt.Should().BeNull();
        reservation.ResolutionReason.Should().BeNull();
        (await verify.StockTransactions.CountAsync()).Should().Be(0);
    }

    private sealed record ExpiredLotSeed(
        int ItemId,
        int LocationId,
        int DestinationLocationId,
        DateTime ExpiryDate,
        string ReservationReference);

    private sealed class RecordingWebhookDispatcher : IWebhookDispatcher
    {
        public List<(string EventType, object Payload)> Events { get; } = [];

        public Task EnqueueAsync<T>(WebhookEvent<T> webhookEvent)
        {
            Events.Add((webhookEvent.EventType, webhookEvent.Payload!));
            return Task.CompletedTask;
        }

        public Task DispatchAsync<T>(WebhookEvent<T> webhookEvent) => Task.CompletedTask;
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

    private static StockService CreateService(
        InventoryDbContext context,
        string tenant,
        IWebhookDispatcher? webhookDispatcher = null) => new(
        new Repository<StockInHand>(context),
        new Repository<StockTransaction>(context),
        new Repository<Item>(context),
        new Repository<Location>(context),
        new Repository<Branch>(context),
        new UnitOfWork(context),
        webhookDispatcher ?? new Mock<IWebhookDispatcher>().Object,
        new TestTenantContext(tenant),
        NullLogger<StockService>.Instance,
        new Repository<StockValuationBucket>(context),
        new Repository<StockValuationEntry>(context),
        reservationRepo: new Repository<StockReservation>(context),
        reservationAllocationRepo: new Repository<StockReservationAllocation>(context));
}

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class StockReservationPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Ordinary_fefo_reservation_skips_expired_lots_when_fresh_stock_is_sufficient()
    {
        fixture.EnsureEnabled();
        var tenant = $"reservation-fefo-skip-expired-{Guid.NewGuid():N}";
        var sourceLineReference = $"fefo-skip-expired-line-{Guid.NewGuid():N}";
        var expiredDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-2), DateTimeKind.Utc);
        var freshDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(20), DateTimeKind.Utc);
        int itemId;
        int locationId;

        await using (var setup = fixture.CreateContext(tenant))
        {
            var item = new Item
            {
                ItemCode = $"FEFO-SKIP-{Guid.NewGuid():N}"[..20],
                Description = "FEFO skips expired stock by default"
            };
            var location = new Location { Name = $"FEFO skip expired {Guid.NewGuid():N}" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
            setup.StockInHand.AddRange(
                new StockInHand
                {
                    ItemId = itemId,
                    LocationId = locationId,
                    Quantity = 2,
                    BatchNumber = "EXPIRED-FEFO",
                    ExpiryDate = expiredDate
                },
                new StockInHand
                {
                    ItemId = itemId,
                    LocationId = locationId,
                    Quantity = 3,
                    BatchNumber = "FRESH-FEFO",
                    ExpiryDate = freshDate
                });
            await setup.SaveChangesAsync();
        }

        await using (var create = fixture.CreateContext(tenant))
        {
            await CreateService(create, tenant).CreateReservationAsync(
                new CreateStockReservationRequest(itemId, locationId, 3, sourceLineReference));
        }

        await using var verify = fixture.CreateContext(tenant);
        var reservation = await CreateService(verify, tenant).GetReservationAsync(sourceLineReference);
        reservation.Should().NotBeNull();
        reservation!.Allocations.Should().NotBeNull();
        reservation.Allocations!.Should().ContainSingle().Which.Should().Be(
            new StockReservationAllocationView("FRESH-FEFO", freshDate, 3, 0, 3, null));
        var stock = await verify.StockInHand
            .Where(row => row.ItemId == itemId && row.LocationId == locationId)
            .OrderBy(row => row.ExpiryDate)
            .ToListAsync();
        stock.Select(row => row.ReservedQuantity).Should().Equal(0, 3);
    }

    [PostgreSqlFact]
    public async Task Reservation_splits_by_fefo_consumes_across_allocations_and_releases_remaining_quantity_idempotently()
    {
        fixture.EnsureEnabled();
        var tenant = $"reservation-fefo-split-{Guid.NewGuid():N}";
        var sourceLineReference = $"fefo-split-line-{Guid.NewGuid():N}";
        var firstExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-2), DateTimeKind.Utc);
        var secondExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(20), DateTimeKind.Utc);
        var thirdExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(30), DateTimeKind.Utc);
        int itemId;
        int locationId;

        await using (var setup = fixture.CreateContext(tenant))
        {
            var item = new Item
            {
                ItemCode = $"FEFO-SPLIT-{Guid.NewGuid():N}"[..20],
                Description = "FEFO split reservation"
            };
            var location = new Location { Name = $"FEFO split {Guid.NewGuid():N}" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
            setup.StockInHand.AddRange(
                new StockInHand
                {
                    ItemId = itemId,
                    LocationId = locationId,
                    Quantity = 2,
                    BatchNumber = "FEFO-001",
                    ExpiryDate = firstExpiry
                },
                new StockInHand
                {
                    ItemId = itemId,
                    LocationId = locationId,
                    Quantity = 3,
                    BatchNumber = "FEFO-002",
                    ExpiryDate = secondExpiry
                },
                new StockInHand
                {
                    ItemId = itemId,
                    LocationId = locationId,
                    Quantity = 4,
                    BatchNumber = "FEFO-003",
                    ExpiryDate = thirdExpiry
                });
            await setup.SaveChangesAsync();
        }

        const string expiredLotReason = "Approved expired-lot reservation for test";
        var request = new CreateStockReservationRequest(
            itemId, locationId, 5, sourceLineReference, ExpiryExceptionReason: expiredLotReason);
        var webhookDispatcher = new RecordingWebhookDispatcher();
        var reservationScope = new StockMutationScope(
            null,
            () => Task.FromResult(true),
            () => Task.FromResult(true));
        await using (var create = fixture.CreateContext(tenant))
            await CreateService(create, tenant, webhookDispatcher).CreateReservationAsync(request, reservationScope);

        // Replaying the same source-line request must not reserve the lots again.
        await using (var replay = fixture.CreateContext(tenant))
            await CreateService(replay, tenant, webhookDispatcher).CreateReservationAsync(request, reservationScope);

        await using (var verifyCreated = fixture.CreateContext(tenant))
        {
            var reservation = await CreateService(verifyCreated, tenant, webhookDispatcher)
                .GetReservationAsync(sourceLineReference);
            reservation.Should().NotBeNull();
            reservation!.SourceLineReference.Should().Be(sourceLineReference);
            reservation.Quantity.Should().Be(5);
            reservation.ConsumedQuantity.Should().Be(0);
            reservation.RemainingQuantity.Should().Be(5);
            reservation.Allocations.Should().NotBeNull();
            reservation.Allocations!.Should().Equal(
                new StockReservationAllocationView("FEFO-001", firstExpiry, 2, 0, 2, expiredLotReason),
                new StockReservationAllocationView("FEFO-002", secondExpiry, 3, 0, 3, null));

            var stock = await verifyCreated.StockInHand
                .Where(row => row.ItemId == itemId && row.LocationId == locationId)
                .OrderBy(row => row.ExpiryDate)
                .ToListAsync();
            stock.Select(row => row.ReservedQuantity).Should().Equal(2, 3, 0);
            (await verifyCreated.StockReservations.CountAsync(row =>
                row.SourceLineReference == sourceLineReference)).Should().Be(1);
            (await verifyCreated.StockReservationAllocations.CountAsync()).Should().Be(2);
        }

        await using (var consume = fixture.CreateContext(tenant))
        {
            await CreateService(consume, tenant, webhookDispatcher).ConsumeReservationAsync(
                new ConsumeStockReservationRequest(
                    sourceLineReference, 3, "partial FEFO consumption", expiredLotReason),
                reservationScope);
        }

        // Replaying creation after consumption must not restore or duplicate allocations.
        await using (var replayAfterConsumption = fixture.CreateContext(tenant))
            await CreateService(replayAfterConsumption, tenant, webhookDispatcher)
                .CreateReservationAsync(request, reservationScope);

        await using (var verifyConsumed = fixture.CreateContext(tenant))
        {
            var reservation = await CreateService(verifyConsumed, tenant, webhookDispatcher)
                .GetReservationAsync(sourceLineReference);
            reservation.Should().NotBeNull();
            reservation!.ConsumedQuantity.Should().Be(3);
            reservation.RemainingQuantity.Should().Be(2);
            reservation.Allocations.Should().NotBeNull();
            reservation.Allocations!.Should().Equal(
                new StockReservationAllocationView("FEFO-001", firstExpiry, 2, 2, 0, expiredLotReason),
                new StockReservationAllocationView("FEFO-002", secondExpiry, 3, 1, 2, null));

            var stock = await verifyConsumed.StockInHand
                .Where(row => row.ItemId == itemId && row.LocationId == locationId)
                .OrderBy(row => row.ExpiryDate)
                .ToListAsync();
            stock.Select(row => row.Quantity).Should().Equal(0, 2, 4);
            stock.Select(row => row.ReservedQuantity).Should().Equal(0, 2, 0);

            var movements = await verifyConsumed.StockTransactions
                .Where(row => row.ItemId == itemId && row.TransactionType == TransactionType.Sell)
                .OrderBy(row => row.ExpiryDate)
                .ToListAsync();
            movements.Should().HaveCount(2);
            movements.Select(row => (row.BatchNumber, row.Quantity))
                .Should().Equal(("FEFO-001", 2), ("FEFO-002", 1));
            movements.Should().OnlyContain(row =>
                !string.IsNullOrWhiteSpace(row.SourceLineReference) &&
                row.SourceLineReference.StartsWith(sourceLineReference, StringComparison.Ordinal));
            movements.Select(row => row.SourceLineReference).Should().OnlyHaveUniqueItems();

            var saleDeliveries = webhookDispatcher.Events
                .Where(delivery => delivery.EventType == "Stock.Sold")
                .ToArray();
            saleDeliveries.Should().HaveCount(2);
            var salePayloads = saleDeliveries.Select(delivery =>
            {
                var payload = JsonSerializer.SerializeToElement(delivery.Payload);
                return (
                    Reservation: payload.GetProperty("ReservationSourceLineReference").GetString(),
                    Movement: payload.GetProperty("MovementSourceLineReference").GetString(),
                    Batch: payload.GetProperty("BatchNumber").GetString(),
                    Quantity: payload.GetProperty("Quantity").GetInt32());
            }).ToArray();
            salePayloads.Select(payload => (payload.Reservation, payload.Batch, payload.Quantity))
                .Should().Equal(
                    (sourceLineReference, "FEFO-001", 2),
                    (sourceLineReference, "FEFO-002", 1));
            salePayloads.Select(payload => payload.Movement)
                .Should().Equal(movements.Select(row => row.SourceLineReference));

            var reservationAudit = await verifyConsumed.AuditLogs
                .Where(row => row.EntityName == nameof(StockReservation) && row.NewValues != null)
                .ToListAsync();
            reservationAudit.Should().Contain(entry =>
                AuditValue(entry.NewValues!, nameof(StockReservation.SourceLineReference)) == sourceLineReference);

            var allocationAudit = await verifyConsumed.AuditLogs
                .Where(row => row.EntityName == nameof(StockReservationAllocation) &&
                    row.Action == "Insert" && row.NewValues != null)
                .OrderBy(row => row.Id)
                .ToListAsync();
            allocationAudit.Should().HaveCount(2);
            allocationAudit.Select(entry => AuditValue(
                    entry.NewValues!, nameof(StockReservationAllocation.BatchNumber)))
                .Should().Equal("FEFO-001", "FEFO-002");

            var movementAudit = await verifyConsumed.AuditLogs
                .Where(row => row.EntityName == nameof(StockTransaction) && row.NewValues != null)
                .ToListAsync();
            movementAudit.Should().HaveCount(2);
            foreach (var movement in movements)
            {
                movementAudit.Should().Contain(entry =>
                    AuditValue(entry.NewValues!, nameof(StockTransaction.SourceLineReference)) ==
                        movement.SourceLineReference &&
                    AuditValue(entry.NewValues!, nameof(StockTransaction.BatchNumber)) == movement.BatchNumber);
            }
        }

        // The expired allocation is now fully consumed, so another partial consume
        // from the fresh lot must not require an expiry override.
        await using (var consumeFresh = fixture.CreateContext(tenant))
            await CreateService(consumeFresh, tenant, webhookDispatcher).ConsumeReservationAsync(
                new ConsumeStockReservationRequest(sourceLineReference, 1, "consume remaining fresh lot"));

        await using (var release = fixture.CreateContext(tenant))
            await CreateService(release, tenant, webhookDispatcher).ReleaseReservationAsync(
                sourceLineReference, "remaining quantity no longer needed");
        int reservationAuditCountAfterRelease;
        await using (var verifyRelease = fixture.CreateContext(tenant))
        {
            var reservation = await verifyRelease.StockReservations
                .SingleAsync(row => row.SourceLineReference == sourceLineReference);
            reservation.Status.Should().Be(StockReservationStatus.Released);
            reservation.ResolutionReason.Should().Be("remaining quantity no longer needed");
            reservationAuditCountAfterRelease = await verifyRelease.AuditLogs
                .CountAsync(row => row.EntityName == nameof(StockReservation));
        }

        await using (var releaseReplay = fixture.CreateContext(tenant))
            await CreateService(releaseReplay, tenant, webhookDispatcher).ReleaseReservationAsync(
                sourceLineReference, "remaining quantity no longer needed");

        await using var verifyReleased = fixture.CreateContext(tenant);
        var released = await CreateService(verifyReleased, tenant, webhookDispatcher)
            .GetReservationAsync(sourceLineReference);
        released.Should().NotBeNull();
        released!.Status.Should().Be(StockReservationStatus.Released);
        released.ConsumedQuantity.Should().Be(4);
        released.RemainingQuantity.Should().Be(1);
        released.Allocations!.Sum(allocation => allocation.RemainingQuantity).Should().Be(1);

        var releaseDelivery = webhookDispatcher.Events
            .Single(delivery => delivery.EventType == "Stock.ReservationReleased");
        var releasePayload = JsonSerializer.SerializeToElement(releaseDelivery.Payload);
        {
            var payload = releasePayload;
            payload.GetProperty("SourceLineReference").GetString().Should().Be(sourceLineReference);
            var releasedLots = payload.GetProperty("Allocations").EnumerateArray().ToArray();
            releasedLots.Should().ContainSingle();
            releasedLots[0].GetProperty("BatchNumber").GetString().Should().Be("FEFO-002");
            releasedLots[0].GetProperty("ReleasedQuantity").GetInt32().Should().Be(1);
        }

        var finalStock = await verifyReleased.StockInHand
            .Where(row => row.ItemId == itemId && row.LocationId == locationId)
            .OrderBy(row => row.ExpiryDate)
            .ToListAsync();
        finalStock.Select(row => row.Quantity).Should().Equal(0, 1, 4);
        finalStock.Select(row => row.ReservedQuantity).Should().Equal(0, 0, 0);
        finalStock.Sum(row => row.Quantity).Should().Be(9 - released.ConsumedQuantity);
        (await verifyReleased.StockReservations.CountAsync(row =>
            row.SourceLineReference == sourceLineReference)).Should().Be(1);
        (await verifyReleased.AuditLogs.CountAsync(row =>
            row.EntityName == nameof(StockReservation))).Should().Be(reservationAuditCountAfterRelease);
        (await verifyReleased.StockReservationAllocations.CountAsync()).Should().Be(2);
        (await verifyReleased.StockTransactions.CountAsync(row => row.ItemId == itemId)).Should().Be(3);
    }

    [PostgreSqlFact]
    public async Task Expired_split_reservation_webhook_identifies_every_released_lot()
    {
        fixture.EnsureEnabled();
        var tenant = $"reservation-expired-split-{Guid.NewGuid():N}";
        var expiry1 = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(20), DateTimeKind.Utc);
        var expiry2 = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(40), DateTimeKind.Utc);
        int itemId;
        int locationId;

        await using (var setup = fixture.CreateContext(tenant))
        {
            var item = new Item
            {
                ItemCode = $"EXPIRED-SPLIT-{Guid.NewGuid():N}"[..20],
                Description = "Expired split reservation"
            };
            var location = new Location { Name = $"Expired split {Guid.NewGuid():N}" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
            setup.StockInHand.AddRange(
                new StockInHand
                {
                    ItemId = itemId,
                    LocationId = locationId,
                    Quantity = 3,
                    ReservedQuantity = 1,
                    BatchNumber = "EXPIRED-001",
                    ExpiryDate = expiry1
                },
                new StockInHand
                {
                    ItemId = itemId,
                    LocationId = locationId,
                    Quantity = 3,
                    ReservedQuantity = 2,
                    BatchNumber = "EXPIRED-002",
                    ExpiryDate = expiry2
                });
            var reservation = new StockReservation
            {
                ItemId = itemId,
                LocationId = locationId,
                SourceLineReference = "expired-split-source-line",
                Quantity = 3,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                Allocations =
                [
                    new StockReservationAllocation
                    {
                        Ordinal = 0,
                        BatchNumber = "EXPIRED-001",
                        ExpiryDate = expiry1,
                        Quantity = 1
                    },
                    new StockReservationAllocation
                    {
                        Ordinal = 1,
                        BatchNumber = "EXPIRED-002",
                        ExpiryDate = expiry2,
                        Quantity = 2
                    }
                ]
            };
            setup.StockReservations.Add(reservation);
            await setup.SaveChangesAsync();
        }

        var webhookDispatcher = new RecordingWebhookDispatcher();
        await using (var post = fixture.CreateContext(tenant))
            await CreateService(post, tenant, webhookDispatcher).SellStockAsync(
                itemId, locationId, 1, "trigger reservation expiry cleanup", "EXPIRED-001", expiry1);

        await using var verify = fixture.CreateContext(tenant);
        var reservationAfterCleanup = await verify.StockReservations
            .SingleAsync(row => row.SourceLineReference == "expired-split-source-line");
        reservationAfterCleanup.Status.Should().Be(StockReservationStatus.Expired);
        var stock = await verify.StockInHand
            .Where(row => row.ItemId == itemId && row.LocationId == locationId)
            .OrderBy(row => row.ExpiryDate)
            .ToListAsync();
        stock.Select(row => row.Quantity).Should().Equal(2, 3);
        stock.Select(row => row.ReservedQuantity).Should().Equal(0, 0);

        var delivery = webhookDispatcher.Events.Single(item => item.EventType == "Stock.ReservationExpired");
        var payload = JsonSerializer.SerializeToElement(delivery.Payload);
        payload.GetProperty("SourceLineReference").GetString().Should().Be("expired-split-source-line");
        payload.GetProperty("ReleasedQuantity").GetInt32().Should().Be(3);
        var allocations = payload.GetProperty("Allocations").EnumerateArray().ToArray();
        allocations.Should().HaveCount(2);
        allocations.Select(allocation => allocation.GetProperty("BatchNumber").GetString())
            .Should().Equal("EXPIRED-001", "EXPIRED-002");
        allocations.Select(allocation => allocation.GetProperty("ReleasedQuantity").GetInt32())
            .Should().Equal(1, 2);
    }

    [PostgreSqlFact]
    public async Task Date_only_lookup_finds_and_canonicalizes_a_legacy_non_midnight_lot()
    {
        fixture.EnsureEnabled();
        var tenant = $"expiry-legacy-{Guid.NewGuid():N}";
        var calendarDate = DateTime.UtcNow.Date.AddDays(30);
        var legacyExpiry = DateTime.SpecifyKind(calendarDate.AddHours(16), DateTimeKind.Utc);
        const string batchNumber = "LOT-LEGACY-TIME";
        int itemId;
        int locationId;
        int stockId;

        await using (var setup = fixture.CreateContext(tenant))
        {
            var item = new Item { ItemCode = $"LEGACY-{Guid.NewGuid():N}", Description = "Legacy expiry" };
            var location = new Location { Name = $"Legacy expiry {Guid.NewGuid():N}" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;

            var stock = new StockInHand
            {
                ItemId = itemId,
                LocationId = locationId,
                Quantity = 4,
                BatchNumber = batchNumber,
                ExpiryDate = calendarDate
            };
            setup.StockInHand.Add(stock);
            await setup.SaveChangesAsync();
            stockId = stock.Id;
        }

        await using (var legacyWriter = fixture.CreateContext(tenant))
        {
            await legacyWriter.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"StockInHand\" SET \"ExpiryDate\" = {legacyExpiry} WHERE \"Id\" = {stockId}");
        }

        await using (var context = fixture.CreateContext(tenant))
        {
            var service = CreateService(context, tenant);
            (await service.GetByItemAndLocationAsync(itemId, locationId, batchNumber, calendarDate))
                .Should().NotBeNull();
            await service.ReceiveStockAsync(itemId, locationId, 2, "same legacy lot", batchNumber, calendarDate);
        }

        await using var verify = fixture.CreateContext(tenant);
        var rows = await verify.StockInHand.Where(stock => stock.ItemId == itemId).ToListAsync();
        rows.Should().ContainSingle();
        rows[0].Quantity.Should().Be(6);
        AssertUtcCalendarDate(rows[0].ExpiryDate, calendarDate);
    }

    [PostgreSqlFact]
    public async Task Unspecified_expiry_dates_are_stored_as_utc_calendar_days_and_still_block_expired_sales()
    {
        fixture.EnsureEnabled();
        var tenant = $"expiry-date-only-{Guid.NewGuid():N}";
        var calendarDate = DateTime.UtcNow.Date.AddDays(-1);
        var unspecifiedInput = DateTime.SpecifyKind(calendarDate.AddHours(16).AddMinutes(45), DateTimeKind.Unspecified);
        const string batchNumber = "LOT-DATE-ONLY";
        int itemId;
        int locationId;

        await using (var setup = fixture.CreateContext(tenant))
        {
            var item = new Item { ItemCode = $"DATE-{Guid.NewGuid():N}", Description = "Date-only expiry", ReorderLevel = 0 };
            var location = new Location { Name = $"Date-only expiry {Guid.NewGuid():N}" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();

            setup.StockInHand.Add(new StockInHand
            {
                ItemId = item.Id,
                LocationId = location.Id,
                Quantity = 5,
                ReservedQuantity = 1,
                BatchNumber = batchNumber,
                ExpiryDate = unspecifiedInput
            });
            setup.StockReservations.Add(new StockReservation
            {
                ItemId = item.Id,
                LocationId = location.Id,
                SourceLineReference = "date-only-expired-reservation",
                BatchNumber = batchNumber,
                ExpiryDate = unspecifiedInput,
                Quantity = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                Status = StockReservationStatus.Active
            });
            setup.StockTransactions.Add(new StockTransaction
            {
                ItemId = item.Id,
                FromLocationId = location.Id,
                Quantity = 5,
                TransactionType = TransactionType.Receive,
                TransactionDate = DateTime.UtcNow,
                BatchNumber = batchNumber,
                ExpiryDate = unspecifiedInput
            });
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
        }

        await using (var verify = fixture.CreateContext(tenant))
        {
            AssertUtcCalendarDate((await verify.StockInHand.SingleAsync()).ExpiryDate, calendarDate);
            AssertUtcCalendarDate((await verify.StockReservations.SingleAsync()).ExpiryDate, calendarDate);
            AssertUtcCalendarDate((await verify.StockTransactions.SingleAsync()).ExpiryDate, calendarDate);
        }

        // A date-only service input still matches the persisted lot identity instead of
        // creating a second timestamptz value with a non-UTC or non-midnight component.
        await using (var context = fixture.CreateContext(tenant))
        {
            await CreateService(context, tenant).ReceiveStockAsync(
                itemId, locationId, 1, "same date-only lot", batchNumber, unspecifiedInput);
        }

        await using (var context = fixture.CreateContext(tenant))
        {
            var action = () => CreateService(context, tenant).SellStockAsync(
                itemId, locationId, 1, "expired date-only lot", batchNumber, unspecifiedInput);
            await action.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("An audit reason is required to use an expired stock lot.");
        }

        await using var final = fixture.CreateContext(tenant);
        var stock = await final.StockInHand.SingleAsync();
        stock.Quantity.Should().Be(6);
        stock.ReservedQuantity.Should().Be(1);
        AssertUtcCalendarDate(stock.ExpiryDate, calendarDate);

        var reservation = await final.StockReservations.SingleAsync();
        reservation.Status.Should().Be(StockReservationStatus.Active);
        AssertUtcCalendarDate(reservation.ExpiryDate, calendarDate);

        var movements = await final.StockTransactions.OrderBy(row => row.Id).ToListAsync();
        movements.Should().HaveCount(2);
        movements.Should().OnlyContain(row => row.TransactionType == TransactionType.Receive);
        movements.Should().OnlyContain(row => row.ExpiryDate.HasValue &&
            row.ExpiryDate.Value.Kind == DateTimeKind.Utc &&
            row.ExpiryDate.Value.TimeOfDay == TimeSpan.Zero &&
            DateOnly.FromDateTime(row.ExpiryDate.Value) == DateOnly.FromDateTime(calendarDate));
    }

    [PostgreSqlFact]
    public async Task Expired_stock_override_requires_capability_and_nonblank_reason_without_side_effects()
    {
        fixture.EnsureEnabled();
        const string tenant = "expired-override-denials";
        var state = await SeedExpiredPostgresLotAsync(fixture, tenant, "denied-expired-line");

        await using (var context = fixture.CreateContext(tenant))
        {
            var postOnlyScope = new StockMutationScope(
                state.CompanyId,
                () => Task.FromResult(true));
            var action = () => CreateService(context, tenant).SellStockAsync(
                state.ItemId,
                state.LocationId,
                1,
                "unauthorized expired sale",
                "LOT-EXPIRED",
                state.ExpiryDate,
                mutationScope: postOnlyScope,
                expiryExceptionReason: "operator-supplied reason is not authorization");

            await action.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("An explicit company-scoped expired-stock override capability is required.");
        }

        await AssertExpiredPostgresLotUnchangedAsync(fixture, tenant, state);

        await using (var context = fixture.CreateContext(tenant))
        {
            var authorizedScope = CreateOverrideScope(state.CompanyId, authorized: true);
            var action = () => CreateService(context, tenant).SellStockAsync(
                state.ItemId,
                state.LocationId,
                1,
                "expired sale without reason",
                "LOT-EXPIRED",
                state.ExpiryDate,
                mutationScope: authorizedScope,
                expiryExceptionReason: "   ");

            await action.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("An audit reason is required to use an expired stock lot.");
        }

        await AssertExpiredPostgresLotUnchangedAsync(fixture, tenant, state);
    }

    [PostgreSqlFact]
    public async Task Authorized_expired_stock_exceptions_are_recorded_for_each_guarded_action()
    {
        fixture.EnsureEnabled();
        const string reason = "QA approved use for recall containment";

        var saleTenant = $"expiry-override-sale-{Guid.NewGuid():N}";
        var sale = await SeedExpiredPostgresLotAsync(fixture, saleTenant, "sale-expired-line");
        await using (var context = fixture.CreateContext(saleTenant))
        {
            await CreateService(context, saleTenant).SellStockAsync(
                sale.ItemId, sale.LocationId, 1, "approved expired sale", "LOT-EXPIRED", sale.ExpiryDate,
                mutationScope: CreateOverrideScope(sale.CompanyId, authorized: true),
                expiryExceptionReason: reason);
        }
        await AssertPostgresMovementHasReasonAsync(fixture, saleTenant, TransactionType.Sell, reason);

        var transferTenant = $"expiry-override-transfer-{Guid.NewGuid():N}";
        var transfer = await SeedExpiredPostgresLotAsync(fixture, transferTenant, "transfer-expired-line");
        await using (var context = fixture.CreateContext(transferTenant))
        {
            await CreateService(context, transferTenant).TransferStockAsync(
                transfer.ItemId, transfer.LocationId, transfer.DestinationLocationId, 1,
                "approved expired transfer", "LOT-EXPIRED", transfer.ExpiryDate,
                CreateOverrideScope(transfer.CompanyId, authorized: true), reason);
        }
        await AssertPostgresMovementHasReasonAsync(fixture, transferTenant, TransactionType.Transfer, reason);

        var reservationTenant = $"expiry-override-reserve-{Guid.NewGuid():N}";
        var reservationState = await SeedExpiredPostgresLotAsync(
            fixture, reservationTenant, "expired-reservation-cleanup");
        const string approvedReservationLine = "approved-expired-reservation";
        await using (var context = fixture.CreateContext(reservationTenant))
        {
            var request = new CreateStockReservationRequest(
                reservationState.ItemId,
                reservationState.LocationId,
                1,
                approvedReservationLine,
                "LOT-EXPIRED",
                reservationState.ExpiryDate,
                ExpiryExceptionReason: reason);
            await CreateService(context, reservationTenant).CreateReservationAsync(
                request, CreateOverrideScope(reservationState.CompanyId, authorized: true));
        }
        await AssertPostgresReservationHasReasonAsync(fixture, reservationTenant, approvedReservationLine, reason);

        var consumeTenant = $"expiry-override-consume-{Guid.NewGuid():N}";
        var consume = await SeedExpiredPostgresLotAsync(
            fixture, consumeTenant, "approved-expired-consumption", reservationExpires: DateTimeOffset.UtcNow.AddHours(1));
        await using (var context = fixture.CreateContext(consumeTenant))
        {
            var request = new ConsumeStockReservationRequest(
                "approved-expired-consumption", 1, "approved expired reservation sale", reason);
            await CreateService(context, consumeTenant).ConsumeReservationAsync(
                request, CreateOverrideScope(consume.CompanyId, authorized: true));
        }
        await AssertPostgresMovementHasReasonAsync(fixture, consumeTenant, TransactionType.Sell, reason);
    }

    [PostgreSqlFact]
    public async Task Automatic_reservation_cannot_use_expired_lot_revealed_by_expired_reservation_cleanup()
    {
        fixture.EnsureEnabled();
        var tenant = $"auto-expired-reservation-{Guid.NewGuid():N}";
        const string staleReservationReference = "expired-fully-reserved-line";
        const string newReservationReference = "auto-selected-expired-line";
        var state = await SeedExpiredPostgresLotAsync(
            fixture,
            tenant,
            staleReservationReference,
            stockQuantity: 5,
            reservedQuantity: 5,
            reservationQuantity: 5);
        const string reason = "Approved after expired-lot safety review";
        var request = new CreateStockReservationRequest(
            state.ItemId, state.LocationId, 1, newReservationReference,
            ExpiryExceptionReason: reason);

        await using (var context = fixture.CreateContext(tenant))
        {
            var action = () => CreateService(context, tenant).CreateReservationAsync(
                request, CreateOverrideScope(state.CompanyId, authorized: false));
            await action.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("An explicit company-scoped expired-stock override capability is required.");
        }
        await AssertAutoExpiredLotUnchangedAsync(
            fixture, tenant, state, staleReservationReference, newReservationReference);

        await using (var context = fixture.CreateContext(tenant))
        {
            var action = () => CreateService(context, tenant).CreateReservationAsync(
                request with { ExpiryExceptionReason = null },
                CreateOverrideScope(state.CompanyId, authorized: true));
            await action.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("No stock is available for the requested lot.");
        }
        await AssertAutoExpiredLotUnchangedAsync(
            fixture, tenant, state, staleReservationReference, newReservationReference);

        await using (var context = fixture.CreateContext(tenant))
        {
            await CreateService(context, tenant).CreateReservationAsync(
                request, CreateOverrideScope(state.CompanyId, authorized: true));
        }

        await using var verify = fixture.CreateContext(tenant);
        var stock = await verify.StockInHand.SingleAsync(row => row.ItemId == state.ItemId);
        stock.Quantity.Should().Be(5);
        stock.ReservedQuantity.Should().Be(1);
        var reservations = await verify.StockReservations
            .OrderBy(row => row.SourceLineReference)
            .ToListAsync();
        reservations.Should().HaveCount(2);
        reservations.Single(row => row.SourceLineReference == staleReservationReference)
            .Status.Should().Be(StockReservationStatus.Expired);
        var created = reservations.Single(row => row.SourceLineReference == newReservationReference);
        created.Status.Should().Be(StockReservationStatus.Active);
        created.BatchNumber.Should().Be("LOT-EXPIRED");
        created.ExpiryDate.Should().Be(state.ExpiryDate);
        created.ExpiryExceptionReason.Should().Be(reason);
        var auditEntries = await verify.AuditLogs
            .Where(row => row.EntityName == nameof(StockReservation) && row.NewValues != null)
            .ToListAsync();
        auditEntries.Should().Contain(entry =>
            AuditValue(entry.NewValues!, nameof(StockReservation.ExpiryExceptionReason)) == reason);
    }

    [PostgreSqlFact]
    public async Task Automatic_reservation_validates_reason_against_the_final_lot_after_cleanup()
    {
        fixture.EnsureEnabled();
        var tenant = $"mixed-expiry-selection-{Guid.NewGuid():N}";
        const string staleReservationReference = "mixed-expiry-stale-line";
        const string newReservationReference = "mixed-expiry-new-line";
        const string reason = "Approved for the expired FEFO lot revealed by cleanup";
        var state = await SeedExpiredPostgresLotAsync(
            fixture,
            tenant,
            staleReservationReference,
            stockQuantity: 5,
            reservedQuantity: 5,
            reservationQuantity: 5);
        var freshExpiry = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(3), DateTimeKind.Utc);
        await using (var context = fixture.CreateContext(tenant))
        {
            context.StockInHand.Add(new StockInHand
            {
                ItemId = state.ItemId,
                LocationId = state.LocationId,
                Quantity = 5,
                BatchNumber = "LOT-FRESH",
                ExpiryDate = freshExpiry
            });
            await context.SaveChangesAsync();
        }

        var request = new CreateStockReservationRequest(
            state.ItemId, state.LocationId, 1, newReservationReference) with
        {
            ExpiryExceptionReason = reason
        };
        await using (var context = fixture.CreateContext(tenant))
        {
            var action = () => CreateService(context, tenant).CreateReservationAsync(
                request, CreateOverrideScope(state.CompanyId, authorized: false));
            await action.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("An explicit company-scoped expired-stock override capability is required.");
        }
        await AssertMixedExpiredReservationSelectionUnchangedAsync(
            fixture, tenant, state, staleReservationReference, newReservationReference);

        await using (var context = fixture.CreateContext(tenant))
        {
            await CreateService(context, tenant).CreateReservationAsync(
                request, CreateOverrideScope(state.CompanyId, authorized: true));
        }

        await using var verify = fixture.CreateContext(tenant);
        var stockRows = await verify.StockInHand.OrderBy(row => row.BatchNumber).ToListAsync();
        var expiredStock = stockRows.Single(row => row.BatchNumber == "LOT-EXPIRED");
        expiredStock.Quantity.Should().Be(5);
        expiredStock.ReservedQuantity.Should().Be(1);
        var freshStock = stockRows.Single(row => row.BatchNumber == "LOT-FRESH");
        freshStock.Quantity.Should().Be(5);
        freshStock.ReservedQuantity.Should().Be(0);
        var staleReservation = await verify.StockReservations.SingleAsync(
            row => row.SourceLineReference == staleReservationReference);
        staleReservation.Status.Should().Be(StockReservationStatus.Expired);
        var createdReservation = await verify.StockReservations.SingleAsync(
            row => row.SourceLineReference == newReservationReference);
        createdReservation.BatchNumber.Should().Be("LOT-EXPIRED");
        createdReservation.ExpiryDate.Should().Be(state.ExpiryDate);
        createdReservation.ExpiryExceptionReason.Should().Be(reason);
    }

    [PostgreSqlFact]
    public async Task Active_expired_reservation_replay_requires_the_same_audit_reason()
    {
        fixture.EnsureEnabled();
        var tenant = $"expired-reservation-replay-{Guid.NewGuid():N}";
        const string reservationReference = "expired-reservation-replay-line";
        const string reason = "Approved expired reservation replay";
        var state = await SeedExpiredPostgresLotAsync(
            fixture, tenant, "stale-expired-reservation", stockQuantity: 5,
            reservedQuantity: 5, reservationQuantity: 5);
        var request = new CreateStockReservationRequest(
            state.ItemId, state.LocationId, 1, reservationReference,
            "LOT-EXPIRED", state.ExpiryDate, ExpiryExceptionReason: reason);

        await using (var context = fixture.CreateContext(tenant))
        {
            await CreateService(context, tenant).CreateReservationAsync(
                request, CreateOverrideScope(state.CompanyId, authorized: true));
        }

        await using (var context = fixture.CreateContext(tenant))
        {
            await CreateService(context, tenant).CreateReservationAsync(
                request with { ExpiryExceptionReason = $"  {reason}  " },
                CreateOverrideScope(state.CompanyId, authorized: true));
        }

        foreach (var mismatchedRequest in new[]
                 {
                     request with { ExpiryExceptionReason = null },
                     request with { ExpiryExceptionReason = "Different audit reason" }
                 })
        {
            await using var context = fixture.CreateContext(tenant);
            var action = () => CreateService(context, tenant).CreateReservationAsync(
                mismatchedRequest, CreateOverrideScope(state.CompanyId, authorized: true));
            await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("The source line already has a different or closed reservation.");
        }

        await AssertPostgresReservationHasReasonAsync(
            fixture, tenant, reservationReference, reason);
    }

    [PostgreSqlFact]
    public async Task Batch_only_expired_lot_requests_validate_persisted_expiry_and_audit_overrides()
    {
        fixture.EnsureEnabled();
        const string reason = "Approved batch-only expiry exception";

        var saleTenant = $"batch-only-expired-sale-{Guid.NewGuid():N}";
        var sale = await SeedExpiredPostgresLotAsync(
            fixture, saleTenant, "stale-sale-reservation", stockQuantity: 5,
            reservedQuantity: 5, reservationQuantity: 5);
        await using (var context = fixture.CreateContext(saleTenant))
        {
            var action = () => CreateService(context, saleTenant).SellStockAsync(
                sale.ItemId, sale.LocationId, 1, "sale without lot identity");
            await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Insufficient stock for sale");
        }
        await AssertFullyReservedExpiredLotUnchangedAsync(fixture, saleTenant, sale);
        await using (var context = fixture.CreateContext(saleTenant))
        {
            var action = () => CreateService(context, saleTenant).SellStockAsync(
                sale.ItemId, sale.LocationId, 1, "batch-only sale", "LOT-EXPIRED",
                mutationScope: new StockMutationScope(sale.CompanyId, () => Task.FromResult(true)),
                expiryExceptionReason: reason);
            await action.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("An explicit company-scoped expired-stock override capability is required.");
        }
        await AssertFullyReservedExpiredLotUnchangedAsync(fixture, saleTenant, sale);
        var saleWebhooks = new RecordingWebhookDispatcher();
        await using (var context = fixture.CreateContext(saleTenant))
        {
            await CreateService(context, saleTenant, saleWebhooks).SellStockAsync(
                sale.ItemId, sale.LocationId, 1, "approved batch-only sale", "LOT-EXPIRED",
                mutationScope: CreateOverrideScope(sale.CompanyId, authorized: true),
                expiryExceptionReason: reason);
        }
        await AssertPostgresMovementHasReasonAsync(
            fixture, saleTenant, TransactionType.Sell, reason, "LOT-EXPIRED", sale.ExpiryDate);
        AssertRecordedWebhookLotIdentity(saleWebhooks, "Stock.Sold", "LOT-EXPIRED", sale.ExpiryDate);

        var transferTenant = $"batch-only-expired-transfer-{Guid.NewGuid():N}";
        var transfer = await SeedExpiredPostgresLotAsync(
            fixture, transferTenant, "stale-transfer-reservation", stockQuantity: 5,
            reservedQuantity: 5, reservationQuantity: 5);
        await using (var context = fixture.CreateContext(transferTenant))
        {
            var action = () => CreateService(context, transferTenant).TransferStockAsync(
                transfer.ItemId, transfer.LocationId, transfer.DestinationLocationId, 1,
                "transfer without lot identity");
            await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Insufficient stock at source location");
        }
        await AssertFullyReservedExpiredLotUnchangedAsync(fixture, transferTenant, transfer);
        await using (var context = fixture.CreateContext(transferTenant))
        {
            var action = () => CreateService(context, transferTenant).TransferStockAsync(
                transfer.ItemId, transfer.LocationId, transfer.DestinationLocationId, 1,
                "batch-only transfer", "LOT-EXPIRED",
                mutationScope: new StockMutationScope(transfer.CompanyId, () => Task.FromResult(true)),
                expiryExceptionReason: reason);
            await action.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("An explicit company-scoped expired-stock override capability is required.");
        }
        await AssertFullyReservedExpiredLotUnchangedAsync(fixture, transferTenant, transfer);
        var transferWebhooks = new RecordingWebhookDispatcher();
        await using (var context = fixture.CreateContext(transferTenant))
        {
            await CreateService(context, transferTenant, transferWebhooks).TransferStockAsync(
                transfer.ItemId, transfer.LocationId, transfer.DestinationLocationId, 1,
                "approved batch-only transfer", "LOT-EXPIRED",
                expiryDate: null,
                mutationScope: CreateOverrideScope(transfer.CompanyId, authorized: true),
                expiryExceptionReason: reason);
        }
        await AssertPostgresMovementHasReasonAsync(
            fixture, transferTenant, TransactionType.Transfer, reason, "LOT-EXPIRED", transfer.ExpiryDate);
        AssertRecordedWebhookLotIdentity(
            transferWebhooks, "Stock.Transferred", "LOT-EXPIRED", transfer.ExpiryDate);
        await using (var verify = fixture.CreateContext(transferTenant))
        {
            var movedLot = await verify.StockInHand.SingleAsync(row =>
                row.LocationId == transfer.DestinationLocationId && row.BatchNumber == "LOT-EXPIRED");
            movedLot.ExpiryDate.Should().Be(transfer.ExpiryDate);
            movedLot.Quantity.Should().Be(1);
        }

        var reservationTenant = $"batch-only-expired-reserve-{Guid.NewGuid():N}";
        var reservationState = await SeedExpiredPostgresLotAsync(
            fixture, reservationTenant, "stale-batch-reservation", stockQuantity: 5,
            reservedQuantity: 5, reservationQuantity: 5);
        const string newLine = "approved-batch-only-expired-reservation";
        var request = new CreateStockReservationRequest(
            reservationState.ItemId, reservationState.LocationId, 1, newLine, "LOT-EXPIRED",
            ExpiryExceptionReason: reason);
        await using (var context = fixture.CreateContext(reservationTenant))
        {
            var action = () => CreateService(context, reservationTenant).CreateReservationAsync(
                request, CreateOverrideScope(reservationState.CompanyId, authorized: false));
            await action.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("An explicit company-scoped expired-stock override capability is required.");
        }
        await AssertFullyReservedExpiredLotUnchangedAsync(fixture, reservationTenant, reservationState);
        await using (var context = fixture.CreateContext(reservationTenant))
        {
            await CreateService(context, reservationTenant).CreateReservationAsync(
                request, CreateOverrideScope(reservationState.CompanyId, authorized: true));
        }
        await AssertPostgresReservationHasReasonAsync(fixture, reservationTenant, newLine, reason);
    }

    [PostgreSqlFact]
    public async Task Expiry_override_reason_migration_refuses_downgrade_while_audit_reasons_exist()
    {
        fixture.EnsureEnabled();
        var schema = $"expiry_reason_down_{Guid.NewGuid():N}";
        var tenant = $"expiry-reason-down-{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            SearchPath = schema
        }.ConnectionString;

        await using (var createSchema = new NpgsqlConnection(fixture.ConnectionString))
        {
            await createSchema.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", createSchema);
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<InventoryDbContext>()
                .UseNpgsql(connectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            await using var context = new InventoryDbContext(options, new TestTenantContext(tenant));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync();

            var item = new Item { ItemCode = $"DOWN-{Guid.NewGuid():N}", Description = "Downgrade guard test" };
            var location = new Location { Name = "Downgrade guard location" };
            context.AddRange(item, location);
            await context.SaveChangesAsync();

            const string reason = "Retain this audit reason during downgrade";
            context.StockTransactions.Add(new StockTransaction
            {
                ItemId = item.Id,
                FromLocationId = location.Id,
                Quantity = 1,
                TransactionType = TransactionType.Receive,
                TransactionDate = DateTime.UtcNow,
                ExpiryExceptionReason = reason
            });
            context.StockReservations.Add(new StockReservation
            {
                ItemId = item.Id,
                LocationId = location.Id,
                SourceLineReference = $"down-guard-{Guid.NewGuid():N}",
                Quantity = 1,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                ExpiryExceptionReason = reason
            });
            await context.SaveChangesAsync();

            const string previousMigration = "20260918020000_AddQuarantinedStockQuantity";
            var failure = await FluentActions.Awaiting(() => migrator.MigrateAsync(previousMigration))
                .Should().ThrowAsync<PostgresException>();
            failure.Which.MessageText.Should().Contain(
                "Cannot downgrade expired-stock override reasons while audit reasons are still persisted.");

            // A downgrade to this target first rolls back the later source-linked-return migration.
            // Reapply it before using the current EF model to verify that the guarded migration kept the reasons.
            await migrator.MigrateAsync();
            context.ChangeTracker.Clear();
            (await context.StockTransactions.SingleAsync()).ExpiryExceptionReason.Should().Be(reason);
            (await context.StockReservations.SingleAsync()).ExpiryExceptionReason.Should().Be(reason);
            (await context.Database.GetAppliedMigrationsAsync())
                .Should().Contain("20260918030000_AddExpiredStockOverrideReasons");
        }
        finally
        {
            await using var dropSchema = new NpgsqlConnection(fixture.ConnectionString);
            await dropSchema.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", dropSchema);
            await command.ExecuteNonQueryAsync();
        }
    }

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

    [PostgreSqlFact]
    public async Task Quarantined_quantity_is_reported_and_unavailable_to_reservations_or_sales()
    {
        fixture.EnsureEnabled();
        var tenant = $"quarantine-availability-{Guid.NewGuid():N}";
        int itemId;
        int locationId;
        await using (var setup = fixture.CreateContext(tenant))
        {
            var item = new Item { ItemCode = $"QUAR-{Guid.NewGuid():N}", Description = "Quarantine availability", ReorderLevel = 0 };
            var location = new Location { Name = "Quarantine availability location" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            setup.StockInHand.Add(new StockInHand
            {
                ItemId = item.Id,
                LocationId = location.Id,
                Quantity = 10,
                ReservedQuantity = 2,
                QuarantinedQuantity = 4
            });
            setup.StockReservations.Add(new StockReservation
            {
                ItemId = item.Id,
                LocationId = location.Id,
                SourceLineReference = "existing-quarantine-reservation",
                Quantity = 2,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                Status = StockReservationStatus.Active
            });
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
        }

        await using (var context = fixture.CreateContext(tenant))
        {
            var service = CreateService(context, tenant);
            var availability = (await service.GetAvailabilityAsync(itemId, locationId)).Single();
            availability.OnHand.Should().Be(10);
            availability.Reserved.Should().Be(2);
            availability.Quarantined.Should().Be(4);
            availability.Available.Should().Be(4);

            await FluentActions.Invoking(() => service.CreateReservationAsync(
                    new CreateStockReservationRequest(itemId, locationId, 5, "quarantine-line")))
                .Should().ThrowAsync<StockAvailabilityConflictException>();
            await FluentActions.Invoking(() => service.SellStockAsync(itemId, locationId, 5, "quarantine sale"))
                .Should().ThrowAsync<StockAvailabilityConflictException>();
            await service.SellStockAsync(itemId, locationId, 4, "available sale");
        }

        await using var verify = fixture.CreateContext(tenant);
        var stock = await verify.StockInHand.SingleAsync();
        stock.Quantity.Should().Be(6);
        stock.ReservedQuantity.Should().Be(2);
        stock.QuarantinedQuantity.Should().Be(4);
        (await verify.StockReservations.SingleAsync()).Status.Should().Be(StockReservationStatus.Active);
    }

    private static async Task<PostgresExpiredLotSeed> SeedExpiredPostgresLotAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenant,
        string reservationReference,
        DateTimeOffset? reservationExpires = null,
        int stockQuantity = 10,
        int reservedQuantity = 2,
        int reservationQuantity = 2)
    {
        await using var context = fixture.CreateContext(tenant);
        var company = new Company
        {
            Code = $"EXP-{Guid.NewGuid():N}"[..12],
            LegalName = "Expired stock exception test company"
        };
        context.Companies.Add(company);
        await context.SaveChangesAsync();

        var branch = new Branch
        {
            CompanyId = company.Id,
            Code = $"BR-{Guid.NewGuid():N}"[..12],
            Name = "Expired stock exception test branch"
        };
        context.Branches.Add(branch);
        await context.SaveChangesAsync();

        var source = new Location { Name = $"Expiry source {Guid.NewGuid():N}", BranchId = branch.Id };
        var destination = new Location { Name = $"Expiry destination {Guid.NewGuid():N}", BranchId = branch.Id };
        context.Locations.AddRange(source, destination);
        var item = new Item
        {
            ItemCode = $"EXP-{Guid.NewGuid():N}"[..16],
            Description = "Expired stock exception test item",
            ReorderLevel = 0
        };
        context.Items.Add(item);
        await context.SaveChangesAsync();

        var expiryDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-1), DateTimeKind.Unspecified);
        context.StockInHand.Add(new StockInHand
        {
            ItemId = item.Id,
            LocationId = source.Id,
            Quantity = stockQuantity,
            ReservedQuantity = reservedQuantity,
            BatchNumber = "LOT-EXPIRED",
            ExpiryDate = expiryDate
        });
        context.StockReservations.Add(new StockReservation
        {
            ItemId = item.Id,
            LocationId = source.Id,
            SourceLineReference = reservationReference,
            BatchNumber = "LOT-EXPIRED",
            ExpiryDate = expiryDate,
            Quantity = reservationQuantity,
            ExpiresAt = reservationExpires ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            Status = StockReservationStatus.Active
        });
        await context.SaveChangesAsync();

        return new PostgresExpiredLotSeed(
            company.Id, item.Id, source.Id, destination.Id, expiryDate, reservationReference);
    }

    private static async Task AssertAutoExpiredLotUnchangedAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenant,
        PostgresExpiredLotSeed state,
        string staleReservationReference,
        string newReservationReference)
    {
        await using var verify = fixture.CreateContext(tenant);
        var stock = await verify.StockInHand.SingleAsync(row => row.ItemId == state.ItemId);
        stock.Quantity.Should().Be(5);
        stock.ReservedQuantity.Should().Be(5);
        var staleReservation = await verify.StockReservations.SingleAsync(
            row => row.SourceLineReference == staleReservationReference);
        staleReservation.Status.Should().Be(StockReservationStatus.Active);
        staleReservation.ConsumedQuantity.Should().Be(0);
        (await verify.StockReservations.CountAsync(row => row.SourceLineReference == newReservationReference))
            .Should().Be(0);
        (await verify.StockTransactions.CountAsync(row => row.ItemId == state.ItemId)).Should().Be(0);
        (await verify.AuditLogs.CountAsync(row => row.EntityName == nameof(StockReservation))).Should().Be(1);
    }

    private static async Task AssertMixedExpiredReservationSelectionUnchangedAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenant,
        PostgresExpiredLotSeed state,
        string staleReservationReference,
        string newReservationReference)
    {
        await using var verify = fixture.CreateContext(tenant);
        var expiredStock = await verify.StockInHand.SingleAsync(row => row.BatchNumber == "LOT-EXPIRED");
        expiredStock.Quantity.Should().Be(5);
        expiredStock.ReservedQuantity.Should().Be(5);
        var freshStock = await verify.StockInHand.SingleAsync(row => row.BatchNumber == "LOT-FRESH");
        freshStock.Quantity.Should().Be(5);
        freshStock.ReservedQuantity.Should().Be(0);
        (await verify.StockReservations.SingleAsync(row =>
            row.SourceLineReference == staleReservationReference)).Status.Should().Be(StockReservationStatus.Active);
        (await verify.StockReservations.CountAsync(row =>
            row.SourceLineReference == newReservationReference)).Should().Be(0);
        (await verify.StockTransactions.CountAsync(row => row.ItemId == state.ItemId)).Should().Be(0);
    }

    private static async Task AssertFullyReservedExpiredLotUnchangedAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenant,
        PostgresExpiredLotSeed state)
    {
        await using var verify = fixture.CreateContext(tenant);
        var stock = await verify.StockInHand.SingleAsync(row => row.ItemId == state.ItemId);
        stock.Quantity.Should().Be(5);
        stock.ReservedQuantity.Should().Be(5);
        var reservation = await verify.StockReservations.SingleAsync(
            row => row.SourceLineReference == state.ReservationReference);
        reservation.Status.Should().Be(StockReservationStatus.Active);
        reservation.Quantity.Should().Be(5);
        reservation.ConsumedQuantity.Should().Be(0);
        (await verify.StockTransactions.CountAsync(row => row.ItemId == state.ItemId)).Should().Be(0);
        (await verify.AuditLogs.CountAsync(row => row.EntityName == nameof(StockTransaction))).Should().Be(0);
        (await verify.AuditLogs.CountAsync(row => row.EntityName == nameof(StockReservation))).Should().Be(1);
    }

    private static StockMutationScope CreateOverrideScope(int companyId, bool authorized) => new(
        companyId,
        () => Task.FromResult(true),
        () => Task.FromResult(authorized));

    private static async Task AssertExpiredPostgresLotUnchangedAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenant,
        PostgresExpiredLotSeed state)
    {
        await using var verify = fixture.CreateContext(tenant);
        var stock = await verify.StockInHand.SingleAsync(row => row.ItemId == state.ItemId);
        stock.Quantity.Should().Be(10);
        stock.ReservedQuantity.Should().Be(2);
        var reservation = await verify.StockReservations.SingleAsync(row =>
            row.SourceLineReference == state.ReservationReference);
        reservation.Status.Should().Be(StockReservationStatus.Active);
        reservation.ConsumedQuantity.Should().Be(0);
        (await verify.StockTransactions.CountAsync(row => row.ItemId == state.ItemId)).Should().Be(0);
        (await verify.AuditLogs.CountAsync(row => row.EntityName == nameof(StockTransaction))).Should().Be(0);
    }

    private static async Task AssertPostgresMovementHasReasonAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenant,
        TransactionType transactionType,
        string reason,
        string? expectedBatchNumber = null,
        DateTime? expectedExpiryDate = null)
    {
        await using var verify = fixture.CreateContext(tenant);
        var transaction = await verify.StockTransactions.SingleAsync(row => row.TransactionType == transactionType);
        transaction.ExpiryExceptionReason.Should().Be(reason);
        if (expectedBatchNumber is not null)
            transaction.BatchNumber.Should().Be(expectedBatchNumber);
        if (expectedExpiryDate.HasValue)
            transaction.ExpiryDate.Should().Be(expectedExpiryDate.Value);
        var entries = await verify.AuditLogs
            .Where(row => row.EntityName == nameof(StockTransaction) && row.NewValues != null)
            .ToListAsync();
        entries.Should().Contain(entry => AuditValue(entry.NewValues!, nameof(StockTransaction.ExpiryExceptionReason)) == reason);
    }

    private static async Task AssertPostgresReservationHasReasonAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenant,
        string sourceLineReference,
        string reason)
    {
        await using var verify = fixture.CreateContext(tenant);
        var reservation = await verify.StockReservations.SingleAsync(row => row.SourceLineReference == sourceLineReference);
        reservation.ExpiryExceptionReason.Should().Be(reason);
        var entries = await verify.AuditLogs
            .Where(row => row.EntityName == nameof(StockReservation) && row.NewValues != null)
            .ToListAsync();
        entries.Should().Contain(entry => AuditValue(entry.NewValues!, nameof(StockReservation.ExpiryExceptionReason)) == reason);
    }

    private static void AssertRecordedWebhookLotIdentity(
        RecordingWebhookDispatcher dispatcher,
        string eventType,
        string expectedBatchNumber,
        DateTime expectedExpiryDate)
    {
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(
            dispatcher.Events.Single(item => item.EventType == eventType).Payload);
        payload.GetProperty("BatchNumber").GetString().Should().Be(expectedBatchNumber);
        payload.GetProperty("ExpiryDate").GetDateTime().Should().Be(expectedExpiryDate);
    }

    private static string? AuditValue(string newValues, string propertyName)
    {
        using var document = System.Text.Json.JsonDocument.Parse(newValues);
        return document.RootElement.GetProperty(propertyName).GetString();
    }

    private sealed record PostgresExpiredLotSeed(
        int CompanyId,
        int ItemId,
        int LocationId,
        int DestinationLocationId,
        DateTime ExpiryDate,
        string ReservationReference);

    private sealed class RecordingWebhookDispatcher : IWebhookDispatcher
    {
        public List<(string EventType, object Payload)> Events { get; } = [];

        public Task EnqueueAsync<T>(WebhookEvent<T> webhookEvent)
        {
            Events.Add((webhookEvent.EventType, webhookEvent.Payload!));
            return Task.CompletedTask;
        }

        public Task DispatchAsync<T>(WebhookEvent<T> webhookEvent) => Task.CompletedTask;
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

    private static StockService CreateService(
        InventoryDbContext context,
        string tenant,
        IWebhookDispatcher? webhookDispatcher = null) => new(
        new Repository<StockInHand>(context),
        new Repository<StockTransaction>(context),
        new Repository<Item>(context),
        new Repository<Location>(context),
        new Repository<Branch>(context),
        new UnitOfWork(context),
        webhookDispatcher ?? new Mock<IWebhookDispatcher>().Object,
        new TestTenantContext(tenant),
        NullLogger<StockService>.Instance,
        new Repository<StockValuationBucket>(context),
        new Repository<StockValuationEntry>(context),
        new Repository<StockReservation>(context),
        new Repository<StockReservationAllocation>(context));

    private static void AssertUtcCalendarDate(DateTime? actual, DateTime expected)
    {
        actual.Should().NotBeNull();
        actual!.Value.Kind.Should().Be(DateTimeKind.Utc);
        actual.Value.TimeOfDay.Should().Be(TimeSpan.Zero);
        DateOnly.FromDateTime(actual.Value).Should().Be(DateOnly.FromDateTime(expected));
    }
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
