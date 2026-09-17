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
                .WithMessage("The selected stock lot has expired and cannot be reserved, sold, or transferred.");
        }

        await AssertExpiredLotStateUnchangedAsync(database, tenant, state);
    }

    [Fact]
    public async Task Automatically_selected_expired_lot_is_rejected_before_expired_reservations_are_released()
    {
        var database = Guid.NewGuid().ToString();
        var tenant = "expired-auto-reserve-test";
        var state = await SeedExpiredLotAsync(database, tenant, "existing-expired-reservation");

        await using (var context = CreateContext(database, tenant))
        {
            var action = () => CreateService(context, tenant).CreateReservationAsync(
                new CreateStockReservationRequest(state.ItemId, state.LocationId, 1, "new-line"));
            await action.Should().ThrowAsync<StockAvailabilityConflictException>()
                .WithMessage("The selected stock lot has expired and cannot be reserved, sold, or transferred.");
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
                .WithMessage("The selected stock lot has expired and cannot be reserved, sold, or transferred.");
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
                .WithMessage("The selected stock lot has expired and cannot be reserved, sold, or transferred.");
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
                .WithMessage("The selected stock lot has expired and cannot be reserved, sold, or transferred.");
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
        new Repository<StockValuationBucket>(context),
        new Repository<StockValuationEntry>(context),
        reservationRepo: new Repository<StockReservation>(context));
}

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class StockReservationPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
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
                .WithMessage("The selected stock lot has expired and cannot be reserved, sold, or transferred.");
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
