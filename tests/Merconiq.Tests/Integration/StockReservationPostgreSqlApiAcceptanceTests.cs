using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class StockReservationPostgreSqlApiAcceptanceTests(PostgreSqlIntegrationFixture fixture) : IDisposable
{
    private const string TenantId = "test-tenant";
    private readonly PostgreSqlCompanyApiFactory _factory = new(fixture);

    [PostgreSqlFact]
    public async Task Authenticated_api_composes_company_scoped_reservation_expiry_fefo_and_exception_flows()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var seed = await SeedAsync(suffix);
        var companyALocations = new Dictionary<int, int>
        {
            [seed.LocationAId] = seed.CompanyAId,
            [seed.LocationBId] = seed.CompanyBId
        };

        using var operatorA = await CreatePersonaAsync(
            "Operator", suffix, seed.CompanyAId, CompanyCapability.View | CompanyCapability.Post);
        using var accountantA = await CreatePersonaAsync(
            "Accountant", suffix, seed.CompanyAId,
            CompanyCapability.View | CompanyCapability.Post | CompanyCapability.OverrideExpiredStock);
        using var operatorB = await CreatePersonaAsync(
            "Operator", $"{suffix}-b", seed.CompanyBId, CompanyCapability.View | CompanyCapability.Post);

        await AssertAvailabilityAsync(operatorA, seed.ItemId, companyALocations,
        [
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EXPIRED", seed.ExpiredDate, 2, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EARLY", seed.EarlyDate, 4, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-MIDDLE", seed.MiddleDate, 4, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-QUARANTINED", seed.LateDate, 3, 0, 1)
        ]);
        await AssertAvailabilityAsync(operatorB, seed.ItemId, companyALocations,
        [
            Lot(seed.CompanyBId, seed.LocationBId, "COMPANY-B-LOT", seed.CompanyBDate, 7, 0, 0)
        ]);
        (await operatorA.GetAsync($"/api/v1/stock/availability?itemId={seed.ItemId}&locationId={seed.LocationBId}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "the signed operator persona has a grant for company A only");

        var beforeCrossCompanyReservation = await ReadSnapshotAsync(seed);
        var crossCompanyReservation = await operatorA.PostAsJsonAsync("/api/v1/stock/reservations", new
        {
            itemId = seed.ItemId,
            locationId = seed.LocationBId,
            quantity = 1,
            sourceLineReference = $"issue-278-cross-company-{suffix}"
        });
        crossCompanyReservation.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "read isolation alone is insufficient if an operator can reserve another company's stock");
        (await ReadSnapshotAsync(seed)).Should().BeEquivalentTo(beforeCrossCompanyReservation,
            "a denied cross-company reservation must not alter company B stock, reservations, audits, or outbox rows");

        // Give one line a short-lived reservation, then exercise its expiry through the
        // authenticated direct-sale route. The API operation also performs expired-reservation cleanup.
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var expiringLine = $"issue-278-expiring-{suffix}";
        var createExpiring = await operatorA.PostAsJsonAsync("/api/v1/stock/reservations", new
        {
            itemId = seed.ItemId,
            locationId = seed.LocationAId,
            quantity = 1,
            sourceLineReference = expiringLine,
            batchNumber = "LOT-EARLY",
            expiryDate = seed.EarlyDate,
            expiresAt
        });
        createExpiring.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertAvailabilityAsync(operatorA, seed.ItemId, companyALocations,
        [
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EXPIRED", seed.ExpiredDate, 2, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EARLY", seed.EarlyDate, 4, 1, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-MIDDLE", seed.MiddleDate, 4, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-QUARANTINED", seed.LateDate, 3, 0, 1)
        ]);

        // Advance only this synthetic row's expiry in PostgreSQL instead of relying
        // on wall-clock scheduling in CI; the authenticated sale below still runs
        // the real expired-reservation cleanup path.
        await using (var expireReservation = fixture.CreateContext(TenantId))
        {
            var reservation = await expireReservation.StockReservations
                .SingleAsync(row => row.SourceLineReference == expiringLine);
            reservation.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await expireReservation.SaveChangesAsync();
        }

        var beforeUnavailableSales = await ReadSnapshotAsync(seed);
        var expiredDirectSale = await operatorA.PostAsJsonAsync("/api/v1/stock/sell", new
        {
            itemId = seed.ItemId,
            locationId = seed.LocationAId,
            quantity = 1,
            notes = "synthetic rejected expired-lot sale",
            batchNumber = "LOT-EXPIRED",
            expiryDate = seed.ExpiredDate
        });
        expiredDirectSale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadSnapshotAsync(seed)).Should().BeEquivalentTo(beforeUnavailableSales,
            "an expired-lot direct sale without an authorized reason must have no persisted side effects");

        var negativeStockSale = await operatorA.PostAsJsonAsync("/api/v1/stock/sell", new
        {
            itemId = seed.ItemId,
            locationId = seed.LocationAId,
            quantity = 5,
            notes = "synthetic rejected oversell",
            batchNumber = "LOT-MIDDLE",
            expiryDate = seed.MiddleDate
        });
        negativeStockSale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadSnapshotAsync(seed)).Should().BeEquivalentTo(beforeUnavailableSales,
            "an oversell must not drive a lot below zero or create audit, transaction, or outbox rows");

        var cleanupSale = await operatorA.PostAsJsonAsync("/api/v1/stock/sell", new
        {
            itemId = seed.ItemId,
            locationId = seed.LocationAId,
            quantity = 1,
            notes = "synthetic sale after reservation expiry",
            batchNumber = "LOT-QUARANTINED",
            expiryDate = seed.LateDate
        });
        cleanupSale.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var verifyExpiry = fixture.CreateContext(TenantId))
        {
            var expiredReservation = await verifyExpiry.StockReservations
                .SingleAsync(row => row.SourceLineReference == expiringLine);
            expiredReservation.Status.Should().Be(StockReservationStatus.Expired);
            expiredReservation.ResolutionReason.Should().Be("Expired");
            var expiredLot = await verifyExpiry.StockInHand.SingleAsync(row =>
                row.ItemId == seed.ItemId && row.LocationId == seed.LocationAId && row.BatchNumber == "LOT-EARLY");
            expiredLot.ReservedQuantity.Should().Be(0);
        }
        await AssertAvailabilityAsync(operatorA, seed.ItemId, companyALocations,
        [
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EXPIRED", seed.ExpiredDate, 2, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EARLY", seed.EarlyDate, 4, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-MIDDLE", seed.MiddleDate, 4, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-QUARANTINED", seed.LateDate, 2, 0, 1)
        ]);

        // Compete two independently sourced API reservations for 5 of the same 9
        // fresh, non-quarantined units. PostgreSQL must persist exactly one winner.
        var raceLineA = $"issue-278-race-a-{suffix}";
        var raceLineB = $"issue-278-race-b-{suffix}";
        var beforeRace = await ReadSnapshotAsync(seed);
        var race = await CreateCompetingReservationsWithObservedLockContentionAsync(
            operatorA, seed, raceLineA, raceLineB);
        race.Select(result => result.Response.StatusCode)
            .Count(status => status == HttpStatusCode.NoContent).Should().Be(1);
        race.Select(result => result.Response.StatusCode)
            .Count(status => status == HttpStatusCode.Conflict).Should().Be(1);

        var winner = race.Single(result => result.Response.StatusCode == HttpStatusCode.NoContent).SourceLineReference;
        var loser = race.Single(result => result.Response.StatusCode == HttpStatusCode.Conflict).SourceLineReference;
        using (var conflict = race.Single(result => result.SourceLineReference == loser).Response)
        {
            using var problem = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
            problem.RootElement.GetProperty("title").GetString().Should().Be("Stock availability conflict");
            problem.RootElement.GetProperty("detail").GetString().Should().Contain("stock");
        }
        race.Single(result => result.SourceLineReference == winner).Response.Dispose();

        var raceReservation = await ReadReservationAsync(operatorA, winner);
        raceReservation.Allocations.Should().Equal(
            new ReservationAllocation("LOT-EARLY", seed.EarlyDate, 4, null),
            new ReservationAllocation("LOT-MIDDLE", seed.MiddleDate, 1, null));
        await AssertAvailabilityAsync(operatorA, seed.ItemId, companyALocations,
        [
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EXPIRED", seed.ExpiredDate, 2, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EARLY", seed.EarlyDate, 4, 4, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-MIDDLE", seed.MiddleDate, 4, 1, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-QUARANTINED", seed.LateDate, 2, 0, 1)
        ]);

        var afterRace = await ReadSnapshotAsync(seed);
        afterRace.Reservations.Should().ContainSingle(row => row.SourceLineReference == winner);
        afterRace.Reservations.Should().NotContain(row => row.SourceLineReference == loser);
        afterRace.Reservations.Should().ContainSingle(row => row.SourceLineReference == expiringLine &&
            row.Status == StockReservationStatus.Expired);
        afterRace.AllocationCount.Should().Be(beforeRace.AllocationCount + 2,
            "only the winning request may persist its two FEFO allocations");
        afterRace.AuditedEntityCount.Should().Be(beforeRace.AuditedEntityCount + 5,
            "one reservation, two allocations, and two stock-counter updates are the winner's complete audited write set");
        afterRace.IdempotencyRecordCount.Should().Be(beforeRace.IdempotencyRecordCount,
            "unkeyed competing requests must not leave an idempotency claim for the rejected request");
        afterRace.StockTransactions.Should().ContainSingle(row => row.TransactionType == TransactionType.Sell &&
            row.BatchNumber == "LOT-QUARANTINED" && row.Quantity == 1);
        afterRace.WebhookEventTypes.Count.Should().Be(4,
            "the expired line, direct sale, and single successful race reservation enqueue events only");

        // A legacy direct sale must not bypass the successful reservation. Its conflict
        // must leave stock, reservation/allocation, audit, transaction, and outbox state intact.
        var beforeBlockedSale = await ReadSnapshotAsync(seed);
        var blockedSale = await operatorA.PostAsJsonAsync("/api/v1/stock/sell", new
        {
            itemId = seed.ItemId,
            locationId = seed.LocationAId,
            quantity = 2,
            notes = "must not consume reserved units",
            batchNumber = "LOT-EARLY",
            expiryDate = seed.EarlyDate
        });
        blockedSale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using (var problem = JsonDocument.Parse(await blockedSale.Content.ReadAsStringAsync()))
            problem.RootElement.GetProperty("detail").GetString().Should().Contain("reserved stock");
        (await ReadSnapshotAsync(seed)).Should().BeEquivalentTo(beforeBlockedSale,
            "a rejected direct sale must have no persisted business or audit side effects");

        // Cancel a second, FEFO-split reservation and prove that only reserved counters
        // are released; on-hand quantities remain unchanged.
        var cancelledLine = $"issue-278-cancelled-{suffix}";
        // The winning reservation leaves three units in LOT-MIDDLE and one available
        // unit in the partially quarantined later lot; four exercises a second FEFO split.
        var createCancelled = await CreateReservationAsync(operatorA, seed, cancelledLine, 4);
        createCancelled.Response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        createCancelled.Response.Dispose();
        var cancelledAllocation = await ReadReservationAsync(operatorA, cancelledLine);
        cancelledAllocation.Allocations.Should().Equal(
            new ReservationAllocation("LOT-MIDDLE", seed.MiddleDate, 3, null),
            new ReservationAllocation("LOT-QUARANTINED", seed.LateDate, 1, null));
        var beforeCancel = await ReadSnapshotAsync(seed);
        var cancel = await operatorA.PostAsJsonAsync("/api/v1/stock/reservations/cancel", new
        {
            sourceLineReference = cancelledLine,
            reason = "synthetic order cancellation"
        });
        cancel.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var verifyCancel = fixture.CreateContext(TenantId))
        {
            var cancelledReservation = await verifyCancel.StockReservations
                .SingleAsync(row => row.SourceLineReference == cancelledLine);
            cancelledReservation.Status.Should().Be(StockReservationStatus.Cancelled);
            cancelledReservation.ResolutionReason.Should().Be("synthetic order cancellation");
        }
        var afterCancel = await ReadSnapshotAsync(seed);
        afterCancel.Lots.Select(row => (row.BatchNumber, row.Quantity))
            .Should().Equal(beforeCancel.Lots.Select(row => (row.BatchNumber, row.Quantity)),
                "cancellation releases reservation counters without consuming on-hand stock");
        afterCancel.StockTransactions.Should().BeEquivalentTo(beforeCancel.StockTransactions,
            "cancellation must not post a stock movement");
        afterCancel.WebhookEventTypes.Count.Should().Be(beforeCancel.WebhookEventTypes.Count + 1,
            "the cancellation emits one lifecycle event and no stock movement event");
        await AssertAvailabilityAsync(operatorA, seed.ItemId, companyALocations,
        [
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EXPIRED", seed.ExpiredDate, 2, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EARLY", seed.EarlyDate, 4, 4, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-MIDDLE", seed.MiddleDate, 4, 1, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-QUARANTINED", seed.LateDate, 2, 0, 1)
        ]);

        // Consume the competing winner across its two FEFO allocations, then replay the
        // same authenticated request/key. The replay must not deduct or audit twice.
        var consumeWinner = new
        {
            sourceLineReference = winner,
            quantity = 5,
            notes = "synthetic shipment",
            expiryExceptionReason = (string?)null
        };
        var consumeKey = $"issue-278-consume-{suffix}";
        var consumeFirst = await PostWithKeyAsync(
            operatorA, "/api/v1/stock/reservations/consume", consumeWinner, consumeKey);
        consumeFirst.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var afterFirstConsume = await ReadSnapshotAsync(seed);
        var consumeReplay = await PostWithKeyAsync(
            operatorA, "/api/v1/stock/reservations/consume", consumeWinner, consumeKey);
        consumeReplay.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadSnapshotAsync(seed)).Should().BeEquivalentTo(afterFirstConsume,
            "a completed PostgreSQL idempotency key replays without a second stock, audit, or outbox effect");
        await AssertAvailabilityAsync(operatorA, seed.ItemId, companyALocations,
        [
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EXPIRED", seed.ExpiredDate, 2, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EARLY", seed.EarlyDate, 0, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-MIDDLE", seed.MiddleDate, 3, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-QUARANTINED", seed.LateDate, 2, 0, 1)
        ]);

        var beforeExpiredRejections = await ReadSnapshotAsync(seed);
        var expiredWithoutReason = await operatorA.PostAsJsonAsync("/api/v1/stock/reservations", new
        {
            itemId = seed.ItemId,
            locationId = seed.LocationAId,
            quantity = 1,
            sourceLineReference = $"issue-278-expired-missing-reason-{suffix}",
            batchNumber = "LOT-EXPIRED",
            expiryDate = seed.ExpiredDate
        });
        expiredWithoutReason.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using (var problem = JsonDocument.Parse(await expiredWithoutReason.Content.ReadAsStringAsync()))
            problem.RootElement.GetProperty("detail").GetString().Should().Contain("audit reason");
        (await ReadSnapshotAsync(seed)).Should().BeEquivalentTo(beforeExpiredRejections,
            "expired-stock rejection must not create a reservation, movement, audit row, or webhook");

        const string expiredExceptionReason = "Synthetic authorized expired-lot exception review";
        var unauthorizedException = await operatorA.PostAsJsonAsync("/api/v1/stock/reservations", new
        {
            itemId = seed.ItemId,
            locationId = seed.LocationAId,
            quantity = 5,
            sourceLineReference = $"issue-278-expired-unauthorized-{suffix}",
            expiryExceptionReason = expiredExceptionReason
        });
        unauthorizedException.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a posting grant and caller-supplied reason do not substitute for the company override grant");
        (await ReadSnapshotAsync(seed)).Should().BeEquivalentTo(beforeExpiredRejections,
            "authorization denial must have no persisted business, audit, or outbox side effects");

        var exceptionLine = $"issue-278-expired-authorized-{suffix}";
        var authorizedException = await accountantA.PostAsJsonAsync("/api/v1/stock/reservations", new
        {
            itemId = seed.ItemId,
            locationId = seed.LocationAId,
            quantity = 5,
            sourceLineReference = exceptionLine,
            expiryExceptionReason = expiredExceptionReason
        });
        authorizedException.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var exceptionReservation = await ReadReservationAsync(accountantA, exceptionLine);
        exceptionReservation.Allocations.Should().Equal(
            new ReservationAllocation("LOT-EXPIRED", seed.ExpiredDate, 2, expiredExceptionReason),
            new ReservationAllocation("LOT-MIDDLE", seed.MiddleDate, 3, null));
        await AssertAvailabilityAsync(accountantA, seed.ItemId, companyALocations,
        [
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EXPIRED", seed.ExpiredDate, 2, 2, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EARLY", seed.EarlyDate, 0, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-MIDDLE", seed.MiddleDate, 3, 3, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-QUARANTINED", seed.LateDate, 2, 0, 1)
        ]);

        await using (var verifyException = fixture.CreateContext(TenantId))
        {
            var auditedAllocation = await verifyException.StockReservationAllocations
                .Include(row => row.Reservation)
                .SingleAsync(row => row.Reservation.SourceLineReference == exceptionLine &&
                                    row.BatchNumber == "LOT-EXPIRED");
            auditedAllocation.ExpiryExceptionReason.Should().Be(expiredExceptionReason);
            var allocationAudit = await verifyException.AuditLogs
                .Where(row => row.EntityName == nameof(StockReservationAllocation) &&
                              row.Action == "Insert" && row.NewValues != null)
                .ToListAsync();
            allocationAudit.Should().Contain(row =>
                AuditIntValue(row.KeyValues, nameof(StockReservationAllocation.Id)) == auditedAllocation.Id &&
                AuditStringValue(row.NewValues, nameof(StockReservationAllocation.BatchNumber)) == "LOT-EXPIRED" &&
                AuditStringValue(row.NewValues, nameof(StockReservationAllocation.ExpiryExceptionReason)) == expiredExceptionReason,
                "the audit record for this exact expired-lot allocation must preserve its lot and approval reason");
        }

        var consumeException = new
        {
            sourceLineReference = exceptionLine,
            quantity = 5,
            notes = "synthetic approved exception shipment",
            expiryExceptionReason = expiredExceptionReason
        };
        var exceptionConsumeKey = $"issue-278-expired-consume-{suffix}";
        var exceptionConsume = await PostWithKeyAsync(
            accountantA, "/api/v1/stock/reservations/consume", consumeException, exceptionConsumeKey);
        exceptionConsume.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var afterExceptionConsume = await ReadSnapshotAsync(seed);
        var exceptionConsumeReplay = await PostWithKeyAsync(
            accountantA, "/api/v1/stock/reservations/consume", consumeException, exceptionConsumeKey);
        exceptionConsumeReplay.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadSnapshotAsync(seed)).Should().BeEquivalentTo(afterExceptionConsume,
            "replaying the authorized split consume must not double-deduct any lot");

        await using (var verifyMovements = fixture.CreateContext(TenantId))
        {
            var exceptionMovements = await verifyMovements.StockTransactions
                .Where(row => row.ItemId == seed.ItemId && row.SourceLineReference != null &&
                              row.SourceLineReference.StartsWith(exceptionLine))
                .OrderBy(row => row.BatchNumber)
                .ToListAsync();
            exceptionMovements.Should().HaveCount(2);
            exceptionMovements.Sum(row => row.Quantity).Should().Be(5,
                "the split movements must equal the requested quantity, not a second deduction");
            exceptionMovements.Should().ContainSingle(row => row.BatchNumber == "LOT-EXPIRED" &&
                row.Quantity == 2 && row.ExpiryExceptionReason == expiredExceptionReason);
            exceptionMovements.Where(row => row.BatchNumber != "LOT-EXPIRED")
                .Should().OnlyContain(row => row.ExpiryExceptionReason == null);
            var movementAudit = await verifyMovements.AuditLogs
                .Where(row => row.EntityName == nameof(StockTransaction) &&
                              row.Action == "Insert" && row.NewValues != null)
                .ToListAsync();
            var expiredMovement = exceptionMovements.Single(row => row.BatchNumber == "LOT-EXPIRED");
            movementAudit.Should().Contain(row =>
                AuditIntValue(row.KeyValues, nameof(StockTransaction.Id)) == expiredMovement.Id &&
                AuditStringValue(row.NewValues, nameof(StockTransaction.BatchNumber)) == "LOT-EXPIRED" &&
                AuditStringValue(row.NewValues, nameof(StockTransaction.ExpiryExceptionReason)) == expiredExceptionReason,
                "the audit record for this exact expired-lot movement must preserve its lot and approval reason");
        }

        await AssertAvailabilityAsync(operatorA, seed.ItemId, companyALocations,
        [
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EXPIRED", seed.ExpiredDate, 0, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-EARLY", seed.EarlyDate, 0, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-MIDDLE", seed.MiddleDate, 0, 0, 0),
            Lot(seed.CompanyAId, seed.LocationAId, "LOT-QUARANTINED", seed.LateDate, 2, 0, 1)
        ]);
        await AssertAvailabilityAsync(operatorB, seed.ItemId, companyALocations,
        [
            Lot(seed.CompanyBId, seed.LocationBId, "COMPANY-B-LOT", seed.CompanyBDate, 7, 0, 0)
        ]);

        var final = await ReadSnapshotAsync(seed);
        final.Lots.Where(row => row.LocationId == seed.LocationAId).Sum(row => row.Quantity).Should().Be(2,
            "expected company A on-hand is 13 seeded units minus 1 direct sale and 10 consumed units; " +
            $"observed={final.Lots.Where(row => row.LocationId == seed.LocationAId).Sum(row => row.Quantity)}");
        final.Lots.Where(row => row.LocationId == seed.LocationBId).Sum(row => row.Quantity).Should().Be(7,
            "expected company B sentinel stock to remain 7; " +
            $"observed={final.Lots.Where(row => row.LocationId == seed.LocationBId).Sum(row => row.Quantity)}");
        final.StockTransactions.Sum(row => row.Quantity).Should().Be(11,
                "one cleanup sale plus 5 ordinary and 5 exception units were consumed exactly once");
        final.Reservations.Should().Contain(row => row.SourceLineReference == exceptionLine &&
            row.Status == StockReservationStatus.Consumed && row.ConsumedQuantity == 5);
    }

    private async Task<HttpClient> CreatePersonaAsync(
        string role,
        string suffix,
        int companyId,
        CompanyCapability capabilities)
    {
        var user = await _factory.EnsurePersonaUserAsync(role, suffix);
        await using (var context = fixture.CreateContext(TenantId))
        {
            context.CompanyMemberships.Add(new CompanyMembership
            {
                CompanyId = companyId,
                UserId = user.Id,
                Capabilities = capabilities,
                IsActive = true
            });
            await context.SaveChangesAsync();
        }

        return _factory.CreateAuthenticatedClient(user, role);
    }

    private async Task<AcceptanceSeed> SeedAsync(string suffix)
    {
        var expiredDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(-2), DateTimeKind.Utc);
        var earlyDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(5), DateTimeKind.Utc);
        var middleDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(12), DateTimeKind.Utc);
        var lateDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(30), DateTimeKind.Utc);
        var companyBDate = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(40), DateTimeKind.Utc);

        await using var context = fixture.CreateContext(TenantId);
        var companyA = new Company { Code = $"I278-A-{suffix[..8]}", LegalName = "Synthetic company A", BaseCurrency = "USD" };
        var companyB = new Company { Code = $"I278-B-{suffix[..8]}", LegalName = "Synthetic company B", BaseCurrency = "USD" };
        var branchA = new Branch { Company = companyA, Code = $"I278-A-{suffix[..8]}", Name = "Synthetic A branch" };
        var branchB = new Branch { Company = companyB, Code = $"I278-B-{suffix[..8]}", Name = "Synthetic B branch" };
        var locationA = new Location { Branch = branchA, Name = $"Synthetic A warehouse {suffix[..8]}" };
        var locationB = new Location { Branch = branchB, Name = $"Synthetic B warehouse {suffix[..8]}" };
        var item = new Item { ItemCode = $"I278-{suffix[..12]}", Description = "Issue 278 synthetic lot item", Rate = 1m };
        context.AddRange(companyA, companyB, branchA, branchB, locationA, locationB, item);
        await context.SaveChangesAsync();

        context.StockInHand.AddRange(
            new StockInHand { ItemId = item.Id, LocationId = locationA.Id, Quantity = 2, BatchNumber = "LOT-EXPIRED", ExpiryDate = expiredDate },
            new StockInHand { ItemId = item.Id, LocationId = locationA.Id, Quantity = 4, BatchNumber = "LOT-EARLY", ExpiryDate = earlyDate },
            new StockInHand { ItemId = item.Id, LocationId = locationA.Id, Quantity = 4, BatchNumber = "LOT-MIDDLE", ExpiryDate = middleDate },
            new StockInHand { ItemId = item.Id, LocationId = locationA.Id, Quantity = 3, QuarantinedQuantity = 1, BatchNumber = "LOT-QUARANTINED", ExpiryDate = lateDate },
            new StockInHand { ItemId = item.Id, LocationId = locationB.Id, Quantity = 7, BatchNumber = "COMPANY-B-LOT", ExpiryDate = companyBDate });
        var subscription = new WebhookSubscription
        {
            EventType = "*",
            Url = "https://example.invalid/issue-278-acceptance"
        };
        context.WebhookSubscriptions.Add(subscription);
        await context.SaveChangesAsync();

        return new AcceptanceSeed(
            companyA.Id, companyB.Id, item.Id, locationA.Id, locationB.Id,
            expiredDate, earlyDate, middleDate, lateDate, companyBDate, subscription.Id);
    }

    private async Task<(string SourceLineReference, HttpResponseMessage Response)[]>
        CreateCompetingReservationsWithObservedLockContentionAsync(
            HttpClient client,
            AcceptanceSeed seed,
            string sourceLineReferenceA,
            string sourceLineReferenceB)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var lockKey = $"stock-location:{TenantId}:{seed.LocationAId}";
        await using (var acquireLock = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))",
                         connection,
                         transaction))
        {
            acquireLock.Parameters.AddWithValue("lock_key", lockKey);
            await acquireLock.ExecuteScalarAsync();
        }

        int lockOwnerPid;
        await using (var readLockOwner = new NpgsqlCommand(
                         "SELECT pg_backend_pid()",
                         connection,
                         transaction))
        {
            lockOwnerPid = Convert.ToInt32(await readLockOwner.ExecuteScalarAsync());
        }

        // Use distinct TestServer clients so each request has an independent handler/connection
        // path; a single client can serialize in-memory requests before both reach PostgreSQL.
        using var secondClient = _factory.CreateClient();
        secondClient.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;

        var startRequests = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequest = Task.Run(async () =>
        {
            await startRequests.Task;
            return await CreateReservationAsync(client, seed, sourceLineReferenceA, 5);
        });
        var secondRequest = Task.Run(async () =>
        {
            await startRequests.Task;
            return await CreateReservationAsync(secondClient, seed, sourceLineReferenceB, 5);
        });
        startRequests.SetResult();

        var observedBothRequestsWaiting = await WaitForAdvisoryLockWaitersAsync(
            connection,
            transaction,
            lockOwnerPid,
            expectedWaiters: 2,
            timeout: TimeSpan.FromSeconds(30));

        await transaction.RollbackAsync();
        var results = await Task.WhenAll(firstRequest, secondRequest);
        if (!observedBothRequestsWaiting)
        {
            foreach (var result in results)
                result.Response.Dispose();
        }
        observedBothRequestsWaiting.Should().BeTrue(
            "both independent API requests must be observed waiting on this exact PostgreSQL location lock before release");
        return results;
    }

    private static async Task<bool> WaitForAdvisoryLockWaitersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int lockOwnerPid,
        int expectedWaiters,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity AS waiting " +
                "WHERE waiting.wait_event_type = 'Lock' AND waiting.wait_event = 'advisory' " +
                "AND @lock_owner_pid = ANY(pg_blocking_pids(waiting.pid))",
                connection,
                transaction);
            command.Parameters.AddWithValue("lock_owner_pid", lockOwnerPid);
            var waiters = Convert.ToInt32(await command.ExecuteScalarAsync());
            if (waiters >= expectedWaiters)
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static async Task<(string SourceLineReference, HttpResponseMessage Response)> CreateReservationAsync(
        HttpClient client,
        AcceptanceSeed seed,
        string sourceLineReference,
        int quantity)
    {
        var response = await client.PostAsJsonAsync("/api/v1/stock/reservations", new
        {
            itemId = seed.ItemId,
            locationId = seed.LocationAId,
            quantity,
            sourceLineReference
        });
        return (sourceLineReference, response);
    }

    private static async Task<HttpResponseMessage> PostWithKeyAsync<T>(
        HttpClient client,
        string path,
        T requestBody,
        string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task AssertAvailabilityAsync(
        HttpClient client,
        int itemId,
        IReadOnlyDictionary<int, int> companyByLocation,
        IReadOnlyCollection<ExpectedLotBalance> expected)
    {
        var response = await client.GetAsync($"/api/v1/stock/availability?itemId={itemId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<List<StockAvailabilityView>>>();
        body.Should().NotBeNull();
        body!.Success.Should().BeTrue();
        body.Data.Should().NotBeNull();

        var observed = body.Data!
            .Select(row => new ExpectedLotBalance(
                companyByLocation.GetValueOrDefault(row.LocationId, -1),
                row.LocationId,
                row.BatchNumber,
                row.ExpiryDate,
                row.OnHand,
                row.Reserved,
                row.Quarantined,
                row.Available))
            .OrderBy(row => row.ExpiryDate)
            .ThenBy(row => row.BatchNumber)
            .ToArray();
        var expectedOrdered = expected.OrderBy(row => row.ExpiryDate).ThenBy(row => row.BatchNumber).ToArray();
        observed.Should().Equal(expectedOrdered,
            $"independent expected per-company/per-lot values must match the authenticated API; expected=[{string.Join("; ", expectedOrdered)}], observed=[{string.Join("; ", observed)}]");
    }

    private static async Task<ReservationView> ReadReservationAsync(HttpClient client, string sourceLineReference)
    {
        var response = await client.GetAsync(
            $"/api/v1/stock/reservations/{Uri.EscapeDataString(sourceLineReference)}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var reservation = document.RootElement.GetProperty("data");
        var allocations = reservation.GetProperty("allocations").EnumerateArray()
            .Select(row => new ReservationAllocation(
                row.GetProperty("batchNumber").GetString(),
                row.GetProperty("expiryDate").ValueKind == JsonValueKind.Null
                    ? null
                    : row.GetProperty("expiryDate").GetDateTime(),
                row.GetProperty("quantity").GetInt32(),
                row.GetProperty("expiryExceptionReason").ValueKind == JsonValueKind.Null
                    ? null
                    : row.GetProperty("expiryExceptionReason").GetString()))
            .ToArray();
        return new ReservationView(allocations);
    }

    private async Task<BusinessSnapshot> ReadSnapshotAsync(AcceptanceSeed seed)
    {
        await using var context = fixture.CreateContext(TenantId);
        var lots = await context.StockInHand
            .Where(row => row.ItemId == seed.ItemId)
            .OrderBy(row => row.LocationId)
            .ThenBy(row => row.ExpiryDate)
            .Select(row => new StoredLot(
                row.LocationId, row.BatchNumber, row.ExpiryDate, row.Quantity,
                row.ReservedQuantity, row.QuarantinedQuantity))
            .ToArrayAsync();
        var reservations = await context.StockReservations
            .Where(row => row.ItemId == seed.ItemId)
            .OrderBy(row => row.SourceLineReference)
            .Select(row => new ReservationState(
                row.SourceLineReference, row.Quantity, row.ConsumedQuantity, row.Status, row.ResolutionReason))
            .ToArrayAsync();
        var transactions = await context.StockTransactions
            .Where(row => row.ItemId == seed.ItemId)
            .OrderBy(row => row.Id)
            .Select(row => new MovementState(
                row.TransactionType, row.BatchNumber, row.Quantity, row.SourceLineReference,
                row.ExpiryExceptionReason))
            .ToArrayAsync();
        var webhookEventTypes = await context.WebhookDeliveries
            .Where(row => row.SubscriptionId == seed.WebhookSubscriptionId)
            .OrderBy(row => row.Id)
            .Select(row => row.EventType)
            .ToArrayAsync();
        var allocationCount = await context.StockReservationAllocations
            .CountAsync(row => row.Reservation.ItemId == seed.ItemId);
        var auditedEntityCount = await context.AuditLogs.CountAsync(row =>
            row.EntityName == nameof(StockInHand) ||
            row.EntityName == nameof(StockReservation) ||
            row.EntityName == nameof(StockReservationAllocation) ||
            row.EntityName == nameof(StockTransaction));
        var idempotencyRecordCount = await context.IdempotencyRecords.CountAsync();

        return new BusinessSnapshot(
            lots, reservations, transactions, webhookEventTypes,
            allocationCount, auditedEntityCount, idempotencyRecordCount);
    }

    private static string? AuditStringValue(string? json, string propertyName)
    {
        if (json is null)
            return null;

        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? AuditIntValue(string? json, string propertyName)
    {
        if (json is null)
            return null;

        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static ExpectedLotBalance Lot(
        int companyId,
        int locationId,
        string batchNumber,
        DateTime expiryDate,
        int onHand,
        int reserved,
        int quarantined) => new(
            companyId, locationId, batchNumber, expiryDate,
            onHand, reserved, quarantined,
            Math.Max(0, onHand - reserved - quarantined));

    public void Dispose() => _factory.Dispose();

    private sealed record AcceptanceSeed(
        int CompanyAId,
        int CompanyBId,
        int ItemId,
        int LocationAId,
        int LocationBId,
        DateTime ExpiredDate,
        DateTime EarlyDate,
        DateTime MiddleDate,
        DateTime LateDate,
        DateTime CompanyBDate,
        int WebhookSubscriptionId);

    private sealed record ExpectedLotBalance(
        int CompanyId,
        int LocationId,
        string? BatchNumber,
        DateTime? ExpiryDate,
        int OnHand,
        int Reserved,
        int Quarantined,
        int Available);

    private sealed record StoredLot(
        int LocationId,
        string? BatchNumber,
        DateTime? ExpiryDate,
        int Quantity,
        int ReservedQuantity,
        int QuarantinedQuantity);

    private sealed record ReservationState(
        string SourceLineReference,
        int Quantity,
        int ConsumedQuantity,
        StockReservationStatus Status,
        string? ResolutionReason);

    private sealed record MovementState(
        TransactionType TransactionType,
        string? BatchNumber,
        int Quantity,
        string? SourceLineReference,
        string? ExpiryExceptionReason);

    private sealed record BusinessSnapshot(
        IReadOnlyList<StoredLot> Lots,
        IReadOnlyList<ReservationState> Reservations,
        IReadOnlyList<MovementState> StockTransactions,
        IReadOnlyList<string> WebhookEventTypes,
        int AllocationCount,
        int AuditedEntityCount,
        int IdempotencyRecordCount);

    private sealed record ReservationView(IReadOnlyList<ReservationAllocation> Allocations);

    private sealed record ReservationAllocation(
        string? BatchNumber,
        DateTime? ExpiryDate,
        int Quantity,
        string? ExpiryExceptionReason);
}
