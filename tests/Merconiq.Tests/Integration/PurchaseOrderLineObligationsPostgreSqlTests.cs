using System.Data.Common;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class PurchaseOrderLineObligationsPostgreSqlTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Line_obligations_reads_order_and_lines_from_one_repeatable_read_snapshot()
    {
        fixture.EnsureEnabled();
        var tenantId = $"po-obligations-snapshot-{Guid.NewGuid():N}";
        int orderId;
        int lineId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var company = new Company
            {
                Code = $"POS-{Guid.NewGuid():N}"[..15],
                LegalName = "PO obligations snapshot company",
                BaseCurrency = "USD"
            };
            var supplier = new Supplier { Name = "PO obligations snapshot supplier" };
            var item = new Item
            {
                ItemCode = $"POI-{Guid.NewGuid():N}"[..18],
                Description = "PO obligations snapshot item",
                Rate = 1m
            };
            setup.AddRange(company, supplier, item);
            await setup.SaveChangesAsync();

            var order = AddOrder(setup, tenantId, company.Id, supplier.Id);
            var line = AddLine(setup, order, item.Id, quantity: 5);
            await setup.SaveChangesAsync();
            orderId = order.Id;
            lineId = line.Id;
        }

        var pause = new PauseAfterPurchaseOrderReadInterceptor();
        await using var readContext = fixture.CreateContext(tenantId, interceptors: pause);
        var readTask = CreateService(readContext, tenantId).GetLineObligationsAsync(orderId);
        PurchaseOrderLineObligations? snapshot = null;
        try
        {
            await pause.Paused.WaitAsync(TimeSpan.FromSeconds(15));

            await using var writeContext = fixture.CreateContext(tenantId);
            await writeContext.OrderDetails
                .Where(line => line.Id == lineId)
                .ExecuteUpdateAsync(update => update.SetProperty(line => line.Quantity, 9));

            pause.Release();
            snapshot = await readTask.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            pause.Release();
            try
            {
                await readTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch
            {
                // Preserve the original assertion/command failure while releasing the read task.
            }
        }

        snapshot.Should().NotBeNull();
        snapshot!.OrderedQuantity.Should().Be(5);
        snapshot.Lines.Should().ContainSingle().Which.OrderedQuantity.Should().Be(5);

        await using var verifyContext = fixture.CreateContext(tenantId);
        var committed = await CreateService(verifyContext, tenantId).GetLineObligationsAsync(orderId);
        committed.Should().NotBeNull();
        committed!.OrderedQuantity.Should().Be(9);
    }

    private static PurchaseOrder AddOrder(
        InventoryDbContext context,
        string tenantId,
        int companyId,
        int supplierId)
    {
        var number = $"PO-O-{Guid.NewGuid():N}"[..20];
        var order = new PurchaseOrder
        {
            PONumber = number,
            OrderDate = new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc),
            SupplierId = supplierId,
            Status = PurchaseOrderStatus.Approved,
            CommercialVersion = 1,
            ApprovedCommercialVersion = 1,
            ApprovedCommercialSnapshotJson = "{\"schemaVersion\":1}",
            CurrencyScale = 2
        };
        var document = DocumentIdentity.Create(
            order.DocumentId,
            tenantId,
            companyId,
            "PurchaseOrder",
            number,
            2026,
            DocumentLifecycleStatus.Active,
            "PurchaseOrderLineObligationsPostgreSqlTests");
        document.PurchaseOrder = order;
        order.DocumentIdentity = document;
        context.DocumentIdentities.Add(document);
        context.PurchaseOrders.Add(order);
        return order;
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

    private static PurchaseOrderService CreateService(InventoryDbContext context, string tenantId)
    {
        var unitOfWork = new UnitOfWork(context);
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

    private sealed class PauseAfterPurchaseOrderReadInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pauseStarted;

        public Task Paused => _paused.Task;

        public void Release() => _release.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"PurchaseOrders\"", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Exchange(ref _pauseStarted, 1) == 0)
            {
                _paused.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

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
