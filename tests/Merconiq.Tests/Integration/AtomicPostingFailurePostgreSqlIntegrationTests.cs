using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Features.Stock.Commands;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
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
public sealed class AtomicPostingFailurePostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task PostgreSQL_receive_failure_after_stock_write_rolls_back_and_retry_applies_once()
    {
        fixture.EnsureEnabled();
        var tenantId = $"issue-273-{Guid.NewGuid():N}";
        var operationKey = $"receive-{Guid.NewGuid():N}";
        var notes = $"issue-273-atomicity-{Guid.NewGuid():N}";
        var scope = $"{tenantId}:POST:/api/v1/stock/receive";
        var suffix = Guid.NewGuid().ToString("N");
        var functionName = $"Issue273RejectCompletion_{suffix}";
        var triggerName = $"Issue273RejectCompletion_{suffix}";
        const int quantity = 12;
        int itemId;
        int locationId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var item = new Item
            {
                ItemCode = $"I273-{suffix[..12]}",
                Description = "Atomic posting regression item",
                ReorderLevel = 0
            };
            var location = new Location { Name = $"I273-{suffix[..12]}" };
            setup.Items.Add(item);
            setup.Locations.Add(location);
            await setup.SaveChangesAsync();
            itemId = item.Id;
            locationId = location.Id;
        }

        var command = new ReceiveStockCommand(itemId, locationId, quantity, notes);
        var requestHash = IdempotencyRequestHasher.Compute(command);

        await using (var failureContext = fixture.CreateContext(tenantId))
        {
            try
            {
                await InstallCompletionFailureAsync(
                    failureContext,
                    functionName,
                    triggerName,
                    tenantId,
                    notes);

                var failure = await FluentActions.Invoking(() => ExecuteReceiveAsync(
                        failureContext,
                        tenantId,
                        scope,
                        operationKey,
                        requestHash,
                        command))
                    .Should()
                    .ThrowAsync<DbUpdateException>();

                var postgresFailure = failure.Which.InnerException
                    .Should()
                    .BeOfType<PostgresException>()
                    .Which;
                postgresFailure.MessageText.Should()
                    .Contain("Injected issue-273 completion failure after stock write");

                await using var afterFailure = fixture.CreateContext(tenantId);
                (await afterFailure.StockInHand.CountAsync(stock =>
                    stock.ItemId == itemId && stock.LocationId == locationId)).Should().Be(0);
                (await afterFailure.StockTransactions.CountAsync(transaction => transaction.Notes == notes))
                    .Should().Be(0);
                (await afterFailure.IdempotencyRecords.SingleAsync(record =>
                    record.Scope == scope && record.Key == operationKey))
                    .Status.Should().Be(IdempotencyRecordStatus.Failed);
            }
            finally
            {
                await RemoveCompletionFailureAsync(failureContext, functionName, triggerName);
            }
        }

        await using (var retryContext = fixture.CreateContext(tenantId))
        {
            await ExecuteReceiveAsync(
                retryContext,
                tenantId,
                scope,
                operationKey,
                requestHash,
                command);
        }

        await using var verify = fixture.CreateContext(tenantId);
        (await verify.StockInHand.CountAsync(stock =>
            stock.ItemId == itemId && stock.LocationId == locationId)).Should().Be(1);
        (await verify.StockInHand.SingleAsync(stock =>
            stock.ItemId == itemId && stock.LocationId == locationId)).Quantity.Should().Be(quantity);

        var movements = await verify.StockTransactions
            .Where(transaction => transaction.ItemId == itemId && transaction.Notes == notes)
            .ToListAsync();
        movements.Should().ContainSingle();
        movements[0].TransactionType.Should().Be(TransactionType.Receive);
        movements[0].Quantity.Should().Be(quantity);

        var completed = await verify.IdempotencyRecords.SingleAsync(record =>
            record.Scope == scope && record.Key == operationKey);
        completed.Status.Should().Be(IdempotencyRecordStatus.Completed);
        completed.AttemptCount.Should().Be(2);
        completed.ResponseStatusCode.Should().Be(StatusCodes.Status204NoContent);
    }

    private static async Task InstallCompletionFailureAsync(
        InventoryDbContext context,
        string functionName,
        string triggerName,
        string tenantId,
        string notes)
    {
        var escapedTenantId = EscapeSqlLiteral(tenantId);
        var escapedNotes = EscapeSqlLiteral(notes);
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
                    RAISE EXCEPTION 'Injected issue-273 completion failure after stock write';
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

    private static Task ExecuteReceiveAsync(
        InventoryDbContext context,
        string tenantId,
        string scope,
        string operationKey,
        string requestHash,
        ReceiveStockCommand command)
    {
        var unitOfWork = new UnitOfWork(context);
        var tenantContext = new TestTenantContext(tenantId);
        var stockService = new StockService(
            new Repository<StockInHand>(context),
            new Repository<StockTransaction>(context),
            new Repository<Item>(context),
            new Repository<Location>(context),
            new Repository<Branch>(context),
            unitOfWork,
            new Mock<IWebhookDispatcher>().Object,
            tenantContext,
            NullLogger<StockService>.Instance,
            new Repository<StockValuationBucket>(context),
            new Repository<StockValuationEntry>(context));
        var handler = new ReceiveStockCommandHandler(
            stockService,
            NullLogger<ReceiveStockCommandHandler>.Instance);
        var idempotencyStore = new IdempotencyKeyStore(context, tenantContext, unitOfWork);

        return idempotencyStore.ExecuteAsync(
            scope,
            operationKey,
            requestHash,
            () => handler.Handle(command, CancellationToken.None));
    }
}
