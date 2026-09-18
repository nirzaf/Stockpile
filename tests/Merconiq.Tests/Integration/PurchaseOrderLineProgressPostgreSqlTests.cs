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
using Merconiq.Web.Security;
using Merconiq.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class PurchaseOrderLineProgressPostgreSqlTests(PostgreSqlIntegrationFixture fixture)
{
    private static readonly PurchaseOrderStatusActor TestActor = new("line-progress-user", "Line Progress User");

    [PostgreSqlFact]
    public async Task Line_progress_rolls_back_on_write_failure_serializes_concurrent_over_receipt_and_replays_duplicates()
    {
        fixture.EnsureEnabled();
        var tenantId = $"po-progress-{Guid.NewGuid():N}";
        int orderId;
        int lineId;
        int duplicateLineId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var company = new Company
            {
                Code = $"PP-{Guid.NewGuid():N}"[..15],
                LegalName = "PO progress integration company",
                BaseCurrency = "USD"
            };
            var supplier = new Supplier { Name = "PO progress integration supplier" };
            var item = new Item
            {
                ItemCode = $"PPI-{Guid.NewGuid():N}"[..18],
                Description = "PO progress integration item",
                Rate = 1m
            };
            setup.AddRange(company, supplier, item);
            await setup.SaveChangesAsync();

            var number = $"PO-PG-{Guid.NewGuid():N}"[..22];
            var order = new PurchaseOrder
            {
                PONumber = number,
                OrderDate = new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc),
                SupplierId = supplier.Id,
                Status = PurchaseOrderStatus.Approved,
                CommercialVersion = 1,
                ApprovedCommercialVersion = 1,
                ApprovedCommercialSnapshotJson = "{\"schemaVersion\":1}",
                CurrencyScale = 2
            };
            var document = DocumentIdentity.Create(
                order.DocumentId,
                tenantId,
                company.Id,
                "PurchaseOrder",
                number,
                2026,
                DocumentLifecycleStatus.Active,
                "PurchaseOrderLineProgressPostgreSqlTests");
            document.PurchaseOrder = order;
            order.DocumentIdentity = document;
            setup.DocumentIdentities.Add(document);
            setup.PurchaseOrders.Add(order);

            var firstLine = AddLine(setup, order, item.Id, quantity: 5);
            var secondLine = AddLine(setup, order, item.Id, quantity: 3);
            await setup.SaveChangesAsync();
            orderId = order.Id;
            lineId = firstLine.Id;
            duplicateLineId = secondLine.Id;
        }

        var functionName = $"merconiq_test_po_progress_failure_{Guid.NewGuid():N}";
        var triggerName = $"merconiq_test_po_progress_trigger_{Guid.NewGuid():N}";
        await CreateFailureTriggerAsync(functionName, triggerName, tenantId, lineId);
        try
        {
            await using var failingContext = fixture.CreateContext(tenantId);
            var failingService = CreateService(failingContext, tenantId);
            var failed = () => failingService.RecordLineProgressAsync(
                orderId, lineId, new PurchaseOrderLineProgressChange(2, 1, 0), TestActor);
            await failed.Should().ThrowAsync<DbUpdateException>();

            await using (var failedRead = fixture.CreateContext(tenantId))
            {
                var failedOrder = await failedRead.PurchaseOrders.SingleAsync(order => order.Id == orderId);
                var failedLine = await failedRead.OrderDetails.SingleAsync(line => line.Id == lineId);
                failedOrder.Status.Should().Be(PurchaseOrderStatus.Approved);
                failedOrder.ReceivingRevision.Should().Be(0);
                failedLine.ReceivedQuantity.Should().Be(0);
                failedLine.AcceptedQuantity.Should().Be(0);
                failedLine.RejectedQuantity.Should().Be(0);
            }
        }
        finally
        {
            await DropFailureTriggerAsync(functionName, triggerName);
        }

        var saveBarrier = new TwoPartySaveBarrier();
        await using var firstContext = fixture.CreateContext(tenantId, interceptors: saveBarrier);
        await using var secondContext = fixture.CreateContext(tenantId, interceptors: saveBarrier);
        var firstService = CreateService(firstContext, tenantId);
        var secondService = CreateService(secondContext, tenantId);
        var concurrentChange = new PurchaseOrderLineProgressChange(4, 4, 0);

        var outcomes = await Task.WhenAll(
            TryRecordAsync(firstService, orderId, lineId, concurrentChange),
            TryRecordAsync(secondService, orderId, lineId, concurrentChange));

        outcomes.Count(succeeded => succeeded).Should().Be(1);
        outcomes.Count(succeeded => !succeeded).Should().Be(1);

        await using (var replayContext = fixture.CreateContext(tenantId))
        {
            var unitOfWork = new UnitOfWork(replayContext);
            var idempotency = new IdempotencyKeyStore(
                replayContext,
                new TestTenantContext(tenantId),
                unitOfWork);
            var service = CreateService(replayContext, tenantId, unitOfWork);
            var change = new PurchaseOrderLineProgressChange(2, 2, 0);
            var calls = 0;
            Task Operation()
            {
                calls++;
                return service.RecordLineProgressAsync(orderId, duplicateLineId, change, TestActor);
            }

            var scope = $"{tenantId}:POST:purchase-order-line-progress:{orderId}:{duplicateLineId}";
            var key = $"duplicate-{Guid.NewGuid():N}";
            var requestHash = IdempotencyRequestHasher.Compute(change);
            await idempotency.ExecuteAsync(scope, key, requestHash, Operation);
            await idempotency.ExecuteAsync(scope, key, requestHash, Operation);
            calls.Should().Be(1);
        }

        await using var finalRead = fixture.CreateContext(tenantId);
        var finalOrder = await finalRead.PurchaseOrders.SingleAsync(order => order.Id == orderId);
        var finalLines = await finalRead.OrderDetails
            .Where(line => line.PurchaseOrderId == orderId)
            .OrderBy(line => line.Id)
            .ToListAsync();
        var concurrentlyUpdated = finalLines.Single(line => line.Id == lineId);
        var replayed = finalLines.Single(line => line.Id == duplicateLineId);
        finalOrder.Status.Should().Be(PurchaseOrderStatus.Approved);
        finalOrder.ReceivingRevision.Should().Be(2);
        concurrentlyUpdated.ReceivedQuantity.Should().Be(4);
        concurrentlyUpdated.AcceptedQuantity.Should().Be(4);
        replayed.ReceivedQuantity.Should().Be(2);
        replayed.AcceptedQuantity.Should().Be(2);
        replayed.RejectedQuantity.Should().Be(0);
    }

    private static OrderDetail AddLine(InventoryDbContext context, PurchaseOrder order, int itemId, int quantity)
    {
        var line = new OrderDetail
        {
            PurchaseOrder = order,
            ItemId = itemId,
            Quantity = quantity,
            UnitPrice = 1m,
            CurrencyScale = 2,
            Direction = DocumentLineDirection.Charge
        };
        var identity = DocumentLineIdentity.Create(
            line.DocumentLineId,
            order.DocumentId,
            context.CurrentTenantId,
            order.DocumentIdentity.CompanyId,
            "PurchaseOrderLine");
        identity.OrderDetail = line;
        line.DocumentLineIdentity = identity;
        order.OrderDetails.Add(line);
        order.DocumentIdentity.Lines.Add(identity);
        context.DocumentLineIdentities.Add(identity);
        context.OrderDetails.Add(line);
        return line;
    }

    private static PurchaseOrderService CreateService(
        InventoryDbContext context,
        string tenantId,
        UnitOfWork? unitOfWork = null)
    {
        unitOfWork ??= new UnitOfWork(context);
        return new PurchaseOrderService(
            new Repository<PurchaseOrder>(context),
            unitOfWork,
            new DocumentIdentityService(context, unitOfWork, new DocumentNumberService(context, unitOfWork)),
            new NoOpWebhookDispatcher(),
            new TestTenantContext(tenantId),
            NullLogger<PurchaseOrderService>.Instance,
            new Repository<AuditLog>(context),
            orderDetailRepository: new Repository<OrderDetail>(context),
            itemRepository: new Repository<Item>(context));
    }

    private static async Task<bool> TryRecordAsync(
        PurchaseOrderService service,
        int orderId,
        int lineId,
        PurchaseOrderLineProgressChange change)
    {
        try
        {
            await service.RecordLineProgressAsync(orderId, lineId, change, TestActor);
            return true;
        }
        catch (ConcurrencyException)
        {
            return false;
        }
    }

    private async Task CreateFailureTriggerAsync(
        string functionName,
        string triggerName,
        string tenantId,
        int lineId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE FUNCTION "{functionName}"() RETURNS trigger
            LANGUAGE plpgsql AS $function$
            BEGIN
                IF NEW."TenantId" = '{tenantId.Replace("'", "''")}' AND NEW."Id" = {lineId} THEN
                    RAISE EXCEPTION 'forced purchase-order line-progress failure';
                END IF;
                RETURN NEW;
            END;
            $function$;
            CREATE TRIGGER "{triggerName}"
            BEFORE UPDATE OF "ReceivedQuantity", "AcceptedQuantity", "RejectedQuantity" ON "OrderDetails"
            FOR EACH ROW EXECUTE FUNCTION "{functionName}"();
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropFailureTriggerAsync(string functionName, string triggerName)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DROP TRIGGER IF EXISTS "{triggerName}" ON "OrderDetails";
            DROP FUNCTION IF EXISTS "{functionName}"();
            """;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TwoPartySaveBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
                _bothArrived.TrySetResult();

            await _bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }

    private sealed class NoOpWebhookDispatcher : IWebhookDispatcher
    {
        public Task EnqueueAsync<T>(WebhookEvent<T> webhookEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DispatchAsync<T>(WebhookEvent<T> webhookEvent) => Task.CompletedTask;
    }
}
