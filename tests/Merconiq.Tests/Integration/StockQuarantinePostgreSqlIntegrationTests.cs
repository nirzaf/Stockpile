using System.Text.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Exceptions;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class StockQuarantinePostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Quarantine_and_release_are_atomic_audited_idempotent_and_preserve_valuation()
    {
        fixture.EnsureEnabled();
        var tenant = $"quarantine-{Guid.NewGuid():N}";
        var (itemId, locationId, expiryDate) = await SeedAsync(tenant);
        var postScope = new StockMutationScope(null, () => Task.FromResult(true));
        var quarantineRequest = new ChangeStockQuarantineRequest(
            itemId, locationId, 6, "quarantine-line-1", "LOT-Q", expiryDate, "Packaging damage review");

        await using (var context = fixture.CreateContext(tenant))
        {
            var service = CreateService(context, tenant);
            var overAvailable = () => service.QuarantineStockAsync(
                quarantineRequest with { Quantity = 8 }, postScope);
            await overAvailable.Should().ThrowAsync<StockAvailabilityConflictException>();
        }
        await AssertStateAsync(tenant, itemId, locationId, onHand: 12, reserved: 3, quarantined: 2, value: 1200m, transactionCount: 0, deliveryCount: 0);

        await using (var context = fixture.CreateContext(tenant))
            await CreateService(context, tenant).QuarantineStockAsync(quarantineRequest, postScope);

        await using (var context = fixture.CreateContext(tenant))
            await CreateService(context, tenant).QuarantineStockAsync(quarantineRequest, postScope);

        await using (var context = fixture.CreateContext(tenant))
        {
            var service = CreateService(context, tenant);
            var mismatchedReplay = () => service.QuarantineStockAsync(
                quarantineRequest with { Reason = "Different source-line intent" }, postScope);
            await mismatchedReplay.Should().ThrowAsync<StockAvailabilityConflictException>();
        }

        await AssertStateAsync(tenant, itemId, locationId, onHand: 12, reserved: 3, quarantined: 8, value: 1200m, transactionCount: 1, deliveryCount: 1);

        await using (var context = fixture.CreateContext(tenant))
        {
            var sale = () => CreateService(context, tenant).SellStockAsync(
                itemId, locationId, 2, "must release first", "LOT-Q", expiryDate, mutationScope: postScope);
            await sale.Should().ThrowAsync<StockAvailabilityConflictException>();
        }

        await using (var context = fixture.CreateContext(tenant))
        {
            var reservation = () => CreateService(context, tenant).CreateReservationAsync(
                new CreateStockReservationRequest(itemId, locationId, 2, "reservation-line-1", "LOT-Q", expiryDate),
                postScope);
            await reservation.Should().ThrowAsync<StockAvailabilityConflictException>();
        }

        var releaseRequest = new ChangeStockQuarantineRequest(
            itemId, locationId, 2, "release-line-1", "LOT-Q", expiryDate, "Inspection completed");
        await using (var context = fixture.CreateContext(tenant))
        {
            var releaseWithoutOverride = () => CreateService(context, tenant).ReleaseQuarantinedStockAsync(
                releaseRequest, postScope);
            await releaseWithoutOverride.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        var deniedOverrideScope = postScope with
        {
            ReauthorizeQuarantinedStockOverride = () => Task.FromResult(false)
        };
        await using (var context = fixture.CreateContext(tenant))
        {
            var releaseWithDeniedOverride = () => CreateService(context, tenant).ReleaseQuarantinedStockAsync(
                releaseRequest, deniedOverrideScope);
            await releaseWithDeniedOverride.Should().ThrowAsync<UnauthorizedAccessException>();
        }
        await AssertStateAsync(tenant, itemId, locationId, onHand: 12, reserved: 3, quarantined: 8, value: 1200m, transactionCount: 1, deliveryCount: 1);

        var authorizedScope = postScope with
        {
            ReauthorizeQuarantinedStockOverride = () => Task.FromResult(true)
        };
        await using (var context = fixture.CreateContext(tenant))
            await CreateService(context, tenant).ReleaseQuarantinedStockAsync(releaseRequest, authorizedScope);

        await using (var context = fixture.CreateContext(tenant))
            await CreateService(context, tenant).ReleaseQuarantinedStockAsync(releaseRequest, authorizedScope);

        await using (var context = fixture.CreateContext(tenant))
        {
            var mismatchedReplay = () => CreateService(context, tenant).ReleaseQuarantinedStockAsync(
                releaseRequest with { Quantity = 1 }, authorizedScope);
            await mismatchedReplay.Should().ThrowAsync<StockAvailabilityConflictException>();
        }

        await AssertStateAsync(tenant, itemId, locationId, onHand: 12, reserved: 3, quarantined: 6, value: 1200m, transactionCount: 2, deliveryCount: 2);

        await using var verify = fixture.CreateContext(tenant);
        var transactions = await verify.StockTransactions.OrderBy(transaction => transaction.Id).ToListAsync();
        transactions[0].TransactionType.Should().Be(TransactionType.Quarantine);
        transactions[1].TransactionType.Should().Be(TransactionType.QuarantineRelease);
        transactions.Should().OnlyContain(transaction =>
            transaction.BatchNumber == "LOT-Q" && transaction.ExpiryDate == expiryDate);
        transactions.Select(transaction => transaction.QuarantineReason)
            .Should().Equal("Packaging damage review", "Inspection completed");
        transactions.Select(transaction => transaction.SourceLineReference)
            .Should().Equal("quarantine-line-1", "release-line-1");

        var deliveries = await verify.WebhookDeliveries.OrderBy(delivery => delivery.EventType).ToListAsync();
        deliveries.Select(delivery => delivery.EventType)
            .Should().BeEquivalentTo("Stock.Quarantined", "Stock.QuarantineReleased");
        var quarantineDelivery = deliveries.Single(delivery => delivery.EventType == "Stock.Quarantined");
        using var payloadDocument = JsonDocument.Parse(quarantineDelivery.Payload);
        payloadDocument.RootElement.GetProperty("Payload").GetProperty("BatchNumber").GetString().Should().Be("LOT-Q");
        payloadDocument.RootElement.GetProperty("Payload").GetProperty("QuarantineReason")
            .GetString().Should().Be("Packaging damage review");
    }

    private async Task<(int ItemId, int LocationId, DateTime ExpiryDate)> SeedAsync(string tenant)
    {
        var expiryDate = DateTime.UtcNow.Date.AddYears(2);
        await using var context = fixture.CreateContext(tenant);
        var item = new Item { ItemCode = $"QUAR-{Guid.NewGuid():N}", Description = "Quarantine test" };
        var location = new Location { Name = $"Quarantine location {Guid.NewGuid():N}" };
        context.Items.Add(item);
        context.Locations.Add(location);
        await context.SaveChangesAsync();

        context.StockInHand.Add(new StockInHand
        {
            ItemId = item.Id,
            LocationId = location.Id,
            Quantity = 12,
            ReservedQuantity = 3,
            QuarantinedQuantity = 2,
            BatchNumber = "LOT-Q",
            ExpiryDate = expiryDate
        });
        context.StockValuationBuckets.Add(new StockValuationBucket
        {
            ItemId = item.Id,
            LocationId = location.Id,
            Quantity = 12,
            Value = 1200m
        });
        context.WebhookSubscriptions.AddRange(
            new WebhookSubscription { EventType = "Stock.Quarantined", Url = "https://example.invalid/quarantine" },
            new WebhookSubscription { EventType = "Stock.QuarantineReleased", Url = "https://example.invalid/quarantine-release" });
        await context.SaveChangesAsync();
        return (item.Id, location.Id, expiryDate);
    }

    private async Task AssertStateAsync(
        string tenant,
        int itemId,
        int locationId,
        int onHand,
        int reserved,
        int quarantined,
        decimal value,
        int transactionCount,
        int deliveryCount)
    {
        await using var context = fixture.CreateContext(tenant);
        var stock = await context.StockInHand.SingleAsync(row => row.ItemId == itemId && row.LocationId == locationId);
        stock.Quantity.Should().Be(onHand);
        stock.ReservedQuantity.Should().Be(reserved);
        stock.QuarantinedQuantity.Should().Be(quarantined);
        var bucket = await context.StockValuationBuckets.SingleAsync(row => row.ItemId == itemId && row.LocationId == locationId);
        bucket.Quantity.Should().Be(onHand);
        bucket.Value.Should().Be(value);
        (await context.StockValuationEntries.CountAsync()).Should().Be(0);
        (await context.StockTransactions.CountAsync()).Should().Be(transactionCount);
        (await context.WebhookDeliveries.CountAsync()).Should().Be(deliveryCount);
    }

    private static StockService CreateService(InventoryDbContext context, string tenant)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var webhookDispatcher = new WebhookDispatcher(
            services,
            Mock.Of<IHttpClientFactory>(),
            NullLogger<WebhookDispatcher>.Instance,
            context);

        return new StockService(
            new Repository<StockInHand>(context),
            new Repository<StockTransaction>(context),
            new Repository<Item>(context),
            new Repository<Location>(context),
            new Repository<Branch>(context),
            new UnitOfWork(context),
            webhookDispatcher,
            new TestTenantContext(tenant),
            NullLogger<StockService>.Instance,
            new Repository<StockValuationBucket>(context),
            new Repository<StockValuationEntry>(context),
            new Repository<StockReservation>(context));
    }
}
