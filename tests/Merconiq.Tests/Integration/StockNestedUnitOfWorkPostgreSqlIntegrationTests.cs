using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class StockNestedUnitOfWorkPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Stock_suboperation_joins_outer_posting_transaction_and_rolls_back_with_it()
    {
        fixture.EnsureEnabled();
        var tenantId = $"issue-273-outer-{Guid.NewGuid():N}";
        var notes = $"issue-273-outer-transaction-{Guid.NewGuid():N}";
        int itemId;
        int locationId;
        int subscriptionId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var item = new Item
            {
                ItemCode = $"I273-{suffix}",
                Description = "Nested unit-of-work transaction item",
                Rate = 10m,
                ReorderLevel = 0
            };
            var location = new Location { Name = $"I273-{suffix}" };
            var subscription = new WebhookSubscription
            {
                Url = "https://example.invalid/issue-273-stock-received",
                EventType = "Stock.Received"
            };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            setup.WebhookSubscriptions.Add(subscription);
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
            subscriptionId = subscription.Id;
        }

        await using (var operation = fixture.CreateContext(tenantId))
        {
            var unitOfWork = new UnitOfWork(operation);
            var stockService = CreateStockService(operation, tenantId, unitOfWork);

            var abort = () => unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                await stockService.ReceiveStockAsync(
                    itemId,
                    locationId,
                    4,
                    notes,
                    unitCost: 10m);

                // The child stock operation saves its entities, but must not commit the
                // transaction owned by this higher-level posting coordinator.
                unitOfWork.HasActiveTransaction.Should().BeTrue();
                (await operation.StockInHand.SingleAsync(stock =>
                    stock.ItemId == itemId && stock.LocationId == locationId)).Quantity.Should().Be(4);
                (await operation.StockTransactions.SingleAsync(transaction => transaction.Notes == notes))
                    .Quantity.Should().Be(4);
                (await operation.StockValuationBuckets.SingleAsync(bucket =>
                    bucket.ItemId == itemId && bucket.LocationId == locationId))
                    .Should().Match<StockValuationBucket>(bucket => bucket.Quantity == 4 && bucket.Value == 40m);
                (await operation.StockValuationEntries.CountAsync(entry =>
                    entry.ItemId == itemId && entry.EntryType == StockValuationEntryType.Receipt)).Should().Be(1);
                (await operation.WebhookDeliveries.CountAsync(delivery =>
                    delivery.SubscriptionId == subscriptionId && delivery.EventType == "Stock.Received")).Should().Be(1);
                (await operation.AuditLogs.CountAsync(audit => audit.EntityName == nameof(StockTransaction)))
                    .Should().Be(1);

                // A separate session cannot observe any of the staged effects before
                // the outer transaction commits.
                await using var concurrentRead = fixture.CreateContext(tenantId);
                (await concurrentRead.StockInHand.CountAsync()).Should().Be(0);
                (await concurrentRead.StockTransactions.CountAsync(transaction => transaction.Notes == notes))
                    .Should().Be(0);
                throw new InvalidOperationException("Injected failure after nested stock posting.");
            });

            await abort.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Injected failure after nested stock posting.");
        }

        await using (var afterAbort = fixture.CreateContext(tenantId))
        {
            (await afterAbort.StockInHand.CountAsync()).Should().Be(0);
            (await afterAbort.StockTransactions.CountAsync(transaction => transaction.Notes == notes)).Should().Be(0);
            (await afterAbort.StockValuationBuckets.CountAsync()).Should().Be(0);
            (await afterAbort.StockValuationEntries.CountAsync()).Should().Be(0);
            (await afterAbort.WebhookDeliveries.CountAsync(delivery =>
                delivery.SubscriptionId == subscriptionId && delivery.EventType == "Stock.Received")).Should().Be(0);
            (await afterAbort.AuditLogs.CountAsync(audit =>
                audit.EntityName == nameof(StockInHand) ||
                audit.EntityName == nameof(StockTransaction) ||
                audit.EntityName == nameof(StockValuationBucket) ||
                audit.EntityName == nameof(StockValuationEntry) ||
                audit.EntityName == nameof(WebhookDelivery))).Should().Be(0);
        }
    }

    private static StockService CreateStockService(
        InventoryDbContext context,
        string tenantId,
        IUnitOfWork unitOfWork)
    {
        var dispatcher = new WebhookDispatcher(
            Mock.Of<IServiceProvider>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<WebhookDispatcher>.Instance,
            context);

        return new StockService(
            new Repository<StockInHand>(context),
            new Repository<StockTransaction>(context),
            new Repository<Item>(context),
            new Repository<Location>(context),
            new Repository<Branch>(context),
            unitOfWork,
            dispatcher,
            new TestTenantContext(tenantId),
            NullLogger<StockService>.Instance,
            new Repository<StockValuationBucket>(context),
            new Repository<StockValuationEntry>(context));
    }
}
