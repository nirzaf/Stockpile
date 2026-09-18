using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Npgsql;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class PurchaseOrderPostingBoundaryPostgreSqlTests(PostgreSqlIntegrationFixture fixture)
{
    private static readonly PurchaseOrderStatusActor TestActor = new("posting-test-user", "Posting Test User");

    [PostgreSqlFact]
    public async Task Purchase_order_status_change_rolls_back_identity_and_order_together_before_retry()
    {
        fixture.EnsureEnabled();
        var tenantId = $"po-boundary-{Guid.NewGuid():N}";
        int purchaseOrderId;
        DocumentIdentityId documentId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var supplier = new Supplier { Name = "Atomic status supplier" };
            var item = new Item { ItemCode = $"ATOMIC-{Guid.NewGuid():N}"[..15], Description = "Atomic status item", Rate = 5m };
            setup.Suppliers.Add(supplier);
            setup.Items.Add(item);
            await setup.SaveChangesAsync();

            var order = new PurchaseOrder
            {
                PONumber = $"ATOMIC-PO-{Guid.NewGuid():N}"[..20],
                OrderDate = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc),
                SupplierId = supplier.Id,
                TotalAmount = 5m,
                Status = PurchaseOrderStatus.Pending
            };
            var identity = DocumentIdentity.Create(
                order.DocumentId,
                tenantId,
                companyId: null,
                "PurchaseOrder",
                order.PONumber,
                2026,
                DocumentLifecycleStatus.Active,
                "PurchaseOrderPostingBoundaryPostgreSqlTests");
            identity.PurchaseOrder = order;
            order.DocumentIdentity = identity;
            setup.DocumentIdentities.Add(identity);
            setup.PurchaseOrders.Add(order);
            await setup.SaveChangesAsync();
            purchaseOrderId = order.Id;
            documentId = order.DocumentId;
        }

        var functionName = $"merconiq_test_po_failure_{Guid.NewGuid():N}";
        var triggerName = $"merconiq_test_po_trigger_{Guid.NewGuid():N}";
        await CreateFailureTriggerAsync(functionName, triggerName, tenantId);
        try
        {
            await using var context = fixture.CreateContext(tenantId);
            var unitOfWork = new UnitOfWork(context);
            var service = new PurchaseOrderService(
                new Repository<PurchaseOrder>(context),
                unitOfWork,
                new DocumentIdentityService(context, unitOfWork, new DocumentNumberService(context, unitOfWork)),
                new NoOpWebhookDispatcher(),
                new TestTenantContext(tenantId),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PurchaseOrderService>.Instance,
                new Repository<AuditLog>(context));

            var failed = () => service.UpdateStatusAsync(purchaseOrderId, nameof(PurchaseOrderStatus.Cancelled), TestActor);
            await failed.Should().ThrowAsync<DbUpdateException>();

            await using (var failedRead = fixture.CreateContext(tenantId))
            {
                var failedOrder = await failedRead.PurchaseOrders.SingleAsync(order => order.Id == purchaseOrderId);
                var failedIdentity = await failedRead.DocumentIdentities.SingleAsync(identity => identity.Id == documentId);
                failedOrder.Status.Should().Be(PurchaseOrderStatus.Pending);
                failedIdentity.Status.Should().Be(DocumentLifecycleStatus.Active);
            }

            await DropFailureTriggerAsync(functionName, triggerName);
            await service.UpdateStatusAsync(purchaseOrderId, nameof(PurchaseOrderStatus.Cancelled), TestActor);

            await using var successfulRead = fixture.CreateContext(tenantId);
            var successfulOrder = await successfulRead.PurchaseOrders.SingleAsync(order => order.Id == purchaseOrderId);
            var successfulIdentity = await successfulRead.DocumentIdentities.SingleAsync(identity => identity.Id == documentId);
            successfulOrder.Status.Should().Be(PurchaseOrderStatus.Cancelled);
            successfulIdentity.Status.Should().Be(DocumentLifecycleStatus.Cancelled);
        }
        finally
        {
            await DropFailureTriggerAsync(functionName, triggerName);
        }
    }

    private async Task CreateFailureTriggerAsync(string functionName, string triggerName, string tenantId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE FUNCTION "{functionName}"() RETURNS trigger
            LANGUAGE plpgsql AS $function$
            BEGIN
                IF NEW."TenantId" = '{tenantId.Replace("'", "''")}' AND NEW."Status" = 'Cancelled' THEN
                    RAISE EXCEPTION 'forced purchase-order status failure';
                END IF;
                RETURN NEW;
            END;
            $function$;
            CREATE TRIGGER "{triggerName}"
            BEFORE UPDATE OF "Status" ON "PurchaseOrders"
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
            DROP TRIGGER IF EXISTS "{triggerName}" ON "PurchaseOrders";
            DROP FUNCTION IF EXISTS "{functionName}"();
            """;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class NoOpWebhookDispatcher : IWebhookDispatcher
    {
        public Task EnqueueAsync<T>(
            WebhookEvent<T> webhookEvent,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DispatchAsync<T>(WebhookEvent<T> webhookEvent) => Task.CompletedTask;
    }
}
