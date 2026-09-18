using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Features.Stock.Commands;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Merconiq.Web.Security;
using Merconiq.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class StockSellAtomicityPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    private static readonly string[] BusinessAuditEntities =
    [
        nameof(StockInHand),
        nameof(StockTransaction),
        nameof(StockValuationBucket),
        nameof(StockValuationEntry),
        nameof(WebhookDelivery)
    ];

    [PostgreSqlFact]
    public async Task PostgreSQL_sell_completion_failure_rolls_back_all_effects_and_retry_applies_once()
    {
        fixture.EnsureEnabled();
        var tenantId = $"issue-273-sell-{Guid.NewGuid():N}";
        var operationKey = $"sell-{Guid.NewGuid():N}";
        var saleNotes = $"issue-273-sell-atomicity-{Guid.NewGuid():N}";
        var scope = $"{tenantId}:POST:/api/v1/stock/sell";
        var suffix = Guid.NewGuid().ToString("N");
        var functionName = $"Issue273SellReject_{suffix[..12]}";
        var triggerName = $"Issue273SellReject_{suffix[..12]}";
        const int initialQuantity = 10;
        const int saleQuantity = 3;
        int itemId;
        int locationId;
        int subscriptionId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var item = new Item
            {
                ItemCode = $"I273S-{suffix[..12]}",
                Description = "Atomic stock sell regression item",
                Rate = 10m,
                ReorderLevel = 0
            };
            var location = new Location { Name = $"I273S-{suffix[..12]}" };
            var subscription = new WebhookSubscription
            {
                Url = "https://example.invalid/stock-sold",
                EventType = "Stock.Sold"
            };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            setup.WebhookSubscriptions.Add(subscription);
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
            subscriptionId = subscription.Id;

            await CreateStockService(setup, tenantId, new UnitOfWork(setup)).ReceiveStockAsync(
                itemId, locationId, initialQuantity, "atomic sell starting balance", unitCost: 10m);
        }

        Dictionary<string, int> baselineAuditCounts;
        await using (var baseline = fixture.CreateContext(tenantId))
        {
            baselineAuditCounts = await ReadBusinessAuditCountsAsync(baseline);
        }

        var mutationScope = new StockMutationScope(null, _ => Task.FromResult(true));
        var command = new SellStockCommand(
            itemId, locationId, saleQuantity, saleNotes, MutationScope: mutationScope);
        var requestHash = IdempotencyRequestHasher.Compute(command);

        await using (var failedAttempt = fixture.CreateContext(tenantId))
        {
            try
            {
                await InstallCompletionFailureAsync(
                    failedAttempt, functionName, triggerName, tenantId, saleNotes);

                var failure = await FluentActions.Invoking(() => ExecuteSellAsync(
                        failedAttempt, tenantId, scope, operationKey, requestHash, command))
                    .Should()
                    .ThrowAsync<DbUpdateException>();

                failure.Which.InnerException.Should()
                    .BeOfType<PostgresException>()
                    .Which.MessageText.Should()
                    .Contain("Injected issue-273 sell completion failure after stock write");
            }
            finally
            {
                await RemoveCompletionFailureAsync(failedAttempt, functionName, triggerName);
            }
        }

        await using (var afterFailure = fixture.CreateContext(tenantId))
        {
            (await afterFailure.StockInHand.SingleAsync(stock =>
                stock.ItemId == itemId && stock.LocationId == locationId)).Quantity
                .Should().Be(initialQuantity);
            (await afterFailure.StockTransactions.CountAsync(transaction =>
                transaction.ItemId == itemId && transaction.Notes == saleNotes)).Should().Be(0);

            var valuation = await afterFailure.StockValuationBuckets.SingleAsync(bucket =>
                bucket.ItemId == itemId && bucket.LocationId == locationId);
            valuation.Quantity.Should().Be(initialQuantity);
            valuation.Value.Should().Be(100m);
            (await afterFailure.StockValuationEntries.CountAsync(entry =>
                entry.ItemId == itemId && entry.EntryType == StockValuationEntryType.Sale)).Should().Be(0);
            (await afterFailure.WebhookDeliveries.CountAsync(delivery =>
                delivery.EventType == "Stock.Sold" && delivery.SubscriptionId == subscriptionId)).Should().Be(0);
            (await ReadBusinessAuditCountsAsync(afterFailure)).Should().BeEquivalentTo(baselineAuditCounts);
            (await CountSaleTransactionAuditsAsync(afterFailure, saleNotes)).Should().Be(0);

            var failedClaim = await afterFailure.IdempotencyRecords.SingleAsync(record =>
                record.Scope == scope && record.Key == operationKey);
            failedClaim.Status.Should().Be(IdempotencyRecordStatus.Failed);
            failedClaim.AttemptCount.Should().Be(1);
        }

        await using (var retry = fixture.CreateContext(tenantId))
        {
            await ExecuteSellAsync(retry, tenantId, scope, operationKey, requestHash, command);
        }

        await using var verify = fixture.CreateContext(tenantId);
        (await verify.StockInHand.SingleAsync(stock =>
            stock.ItemId == itemId && stock.LocationId == locationId)).Quantity
            .Should().Be(initialQuantity - saleQuantity);

        var sales = await verify.StockTransactions
            .Where(transaction => transaction.ItemId == itemId && transaction.Notes == saleNotes)
            .ToListAsync();
        sales.Should().ContainSingle();
        sales[0].TransactionType.Should().Be(TransactionType.Sell);
        sales[0].Quantity.Should().Be(saleQuantity);

        var finalValuation = await verify.StockValuationBuckets.SingleAsync(bucket =>
            bucket.ItemId == itemId && bucket.LocationId == locationId);
        finalValuation.Quantity.Should().Be(initialQuantity - saleQuantity);
        finalValuation.Value.Should().Be(70m);
        var saleEntries = await verify.StockValuationEntries
            .Where(entry => entry.ItemId == itemId && entry.EntryType == StockValuationEntryType.Sale)
            .ToListAsync();
        saleEntries.Should().ContainSingle();
        saleEntries[0].Quantity.Should().Be(saleQuantity);
        saleEntries[0].TotalValue.Should().Be(30m);

        (await verify.WebhookDeliveries.CountAsync(delivery =>
            delivery.EventType == "Stock.Sold" && delivery.SubscriptionId == subscriptionId)).Should().Be(1);
        (await CountSaleTransactionAuditsAsync(verify, saleNotes)).Should().Be(1);
        var finalAuditCounts = await ReadBusinessAuditCountsAsync(verify);
        foreach (var entityName in BusinessAuditEntities)
        {
            finalAuditCounts[entityName].Should().Be(baselineAuditCounts[entityName] + 1);
        }

        var completed = await verify.IdempotencyRecords.SingleAsync(record =>
            record.Scope == scope && record.Key == operationKey);
        completed.Status.Should().Be(IdempotencyRecordStatus.Completed);
        completed.AttemptCount.Should().Be(2);
        completed.ResponseStatusCode.Should().Be(StatusCodes.Status204NoContent);
    }

    private static async Task ExecuteSellAsync(
        InventoryDbContext context,
        string tenantId,
        string scope,
        string operationKey,
        string requestHash,
        SellStockCommand command)
    {
        var tenantContext = new TestTenantContext(tenantId);
        var unitOfWork = new UnitOfWork(context);
        var handler = new SellStockCommandHandler(
            CreateStockService(context, tenantId, unitOfWork),
            NullLogger<SellStockCommandHandler>.Instance);
        var idempotencyStore = new IdempotencyKeyStore(context, tenantContext, unitOfWork);

        await idempotencyStore.ExecuteAsync(
            scope,
            operationKey,
            requestHash,
            () => handler.Handle(command, CancellationToken.None));
    }

    private static StockService CreateStockService(
        InventoryDbContext context,
        string tenantId,
        IUnitOfWork unitOfWork)
    {
        var webhookDispatcher = new WebhookDispatcher(
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
            webhookDispatcher,
            new TestTenantContext(tenantId),
            NullLogger<StockService>.Instance,
            new Repository<StockValuationBucket>(context),
            new Repository<StockValuationEntry>(context));
    }

    private static async Task<Dictionary<string, int>> ReadBusinessAuditCountsAsync(InventoryDbContext context)
    {
        var counts = BusinessAuditEntities.ToDictionary(entityName => entityName, _ => 0);
        var auditLogs = await context.AuditLogs.AsNoTracking()
            .Where(audit => BusinessAuditEntities.Contains(audit.EntityName))
            .ToListAsync();
        foreach (var group in auditLogs.GroupBy(audit => audit.EntityName))
        {
            counts[group.Key] = group.Count();
        }

        return counts;
    }

    private static async Task<int> CountSaleTransactionAuditsAsync(
        InventoryDbContext context,
        string saleNotes)
    {
        var transactionAudits = await context.AuditLogs.AsNoTracking()
            .Where(audit => audit.EntityName == nameof(StockTransaction))
            .ToListAsync();
        return transactionAudits.Count(audit =>
            audit.NewValues?.Contains(saleNotes, StringComparison.Ordinal) == true);
    }

    private static async Task InstallCompletionFailureAsync(
        InventoryDbContext context,
        string functionName,
        string triggerName,
        string tenantId,
        string saleNotes)
    {
        var escapedTenantId = EscapeSqlLiteral(tenantId);
        var escapedNotes = EscapeSqlLiteral(saleNotes);
        await ExecuteDdlAsync(context, $"""
            CREATE FUNCTION "{functionName}"() RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                IF NEW."Status" = 'Completed'
                   AND NEW."TenantId" = '{escapedTenantId}'
                   AND EXISTS (
                       SELECT 1
                       FROM "StockTransactions" AS stock_tx
                       WHERE stock_tx."TenantId" = NEW."TenantId"
                         AND stock_tx."Notes" = '{escapedNotes}')
                THEN
                    RAISE EXCEPTION 'Injected issue-273 sell completion failure after stock write';
                END IF;
                RETURN NEW;
            END;
            $function$;
            """);

        await ExecuteDdlAsync(context, $"""
            CREATE TRIGGER "{triggerName}"
            BEFORE UPDATE OF "Status" ON "IdempotencyRecords"
            FOR EACH ROW
            EXECUTE FUNCTION "{functionName}"();
            """);
    }

    private static async Task RemoveCompletionFailureAsync(
        InventoryDbContext context,
        string functionName,
        string triggerName)
    {
        await ExecuteDdlAsync(
            context,
            $"DROP TRIGGER IF EXISTS \"{triggerName}\" ON \"IdempotencyRecords\"");
        await ExecuteDdlAsync(context, $"DROP FUNCTION IF EXISTS \"{functionName}\"()");
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static async Task ExecuteDdlAsync(InventoryDbContext context, string commandText)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = commandText;
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
