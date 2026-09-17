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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Moq;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class PurchaseOrderApprovalPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Migration_backfills_approval_snapshot_before_enabling_the_database_guard()
    {
        fixture.EnsureEnabled();
        var schema = $"po_approval_{Guid.NewGuid():N}";
        var tenantId = $"po-approval-migration-{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { SearchPath = schema };
        await using (var createSchema = new NpgsqlConnection(fixture.ConnectionString))
        {
            await createSchema.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", createSchema);
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<InventoryDbContext>()
                .UseNpgsql(connectionString.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            await using var context = new InventoryDbContext(options, new TestTenantContext(tenantId));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260918000000_AddMasterDataExternalIds");

            var unit = new UnitOfMeasure
            {
                ExternalId = $"UOM-{Guid.NewGuid():N}", Code = "EA", Name = "Each",
                DecimalPlaces = 0, IsWholeUnitOnly = true
            };
            var supplier = new Supplier { Name = "Legacy approved supplier", Address = "Dock 7", Email = "legacy@example.test" };
            context.UnitsOfMeasure.Add(unit);
            context.Suppliers.Add(supplier);
            await context.SaveChangesAsync();

            var item = new Item
            {
                ItemCode = $"OLD-{Guid.NewGuid():N}"[..20], Description = "Legacy approved item",
                Rate = 4m, BaseUnitId = unit.Id, PurchaseUnitId = unit.Id,
                PurchaseToBaseFactor = 1m, QuantityPrecision = 0, WholeUnitOnly = true
            };
            context.Items.Add(item);
            var number = $"PO-{Guid.NewGuid():N}"[..20];
            var document = DocumentIdentity.Create(
                DocumentIdentityId.New(), tenantId, null, "PurchaseOrder", number, 2026,
                DocumentLifecycleStatus.Active, "PurchaseOrderApprovalBackfillTest");
            context.DocumentIdentities.Add(document);
            var lineIdentity = DocumentLineIdentity.Create(
                DocumentLineIdentityId.New(), document.Id, tenantId, null, "PurchaseOrderLine");
            context.DocumentLineIdentities.Add(lineIdentity);
            await context.SaveChangesAsync();

            await context.Database.OpenConnectionAsync();
            try
            {
                var connection = (NpgsqlConnection)context.Database.GetDbConnection();
                int purchaseOrderId;
                await using (var insertOrder = new NpgsqlCommand(
                    """
                    INSERT INTO "PurchaseOrders" (
                        "PONumber", "OrderDate", "SupplierId", "TotalAmount", "NetAmount",
                        "DiscountAmount", "TaxAmount", "CurrencyScale", "CalculationVersion",
                        "Status", "Notes", "TenantId", "CreatedAt", "DocumentId")
                    VALUES (@number, @date, @supplier, 8, 8, 0, 0, 2, 1,
                        'Approved', 'legacy memo', @tenant, now(), @document)
                    RETURNING "Id"
                    """,
                    connection))
                {
                    insertOrder.Parameters.AddWithValue("number", number);
                    insertOrder.Parameters.AddWithValue("date", new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc));
                    insertOrder.Parameters.AddWithValue("supplier", supplier.Id);
                    insertOrder.Parameters.AddWithValue("tenant", tenantId);
                    insertOrder.Parameters.AddWithValue("document", document.Id.Value);
                    purchaseOrderId = (int)(await insertOrder.ExecuteScalarAsync())!;
                }

                await using var insertLine = new NpgsqlCommand(
                    """
                    INSERT INTO "OrderDetails" (
                        "PurchaseOrderId", "ItemId", "Quantity", "UnitPrice", "TaxCategory",
                        "TaxMode", "Direction", "DiscountPercent", "TaxRatePercent", "CurrencyScale",
                        "CalculationVersion", "NetAmount", "DiscountAmount", "TaxableAmount",
                        "TaxAmount", "GrossAmount", "DocumentLineId", "TenantId", "CreatedAt")
                    VALUES (@order, @item, 2, 4, 'Standard', 'Exclusive', 'Charge', 0, 0, 2,
                        1, 8, 0, 8, 0, 8, @line, @tenant, now())
                    """,
                    connection);
                insertLine.Parameters.AddWithValue("order", purchaseOrderId);
                insertLine.Parameters.AddWithValue("item", item.Id);
                insertLine.Parameters.AddWithValue("line", lineIdentity.Id.Value);
                insertLine.Parameters.AddWithValue("tenant", tenantId);
                await insertLine.ExecuteNonQueryAsync();
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }

            await migrator.MigrateAsync();
            context.ChangeTracker.Clear();
            var upgraded = await context.PurchaseOrders.SingleAsync(po => po.PONumber == number);
            upgraded.Status.Should().Be(PurchaseOrderStatus.Approved);
            upgraded.CommercialVersion.Should().Be(1);
            upgraded.ApprovedCommercialVersion.Should().Be(1);
            using var snapshot = System.Text.Json.JsonDocument.Parse(upgraded.ApprovedCommercialSnapshotJson!);
            snapshot.RootElement.GetProperty("supplierName").GetString().Should().Be("Legacy approved supplier");
            snapshot.RootElement.GetProperty("companyId").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
            snapshot.RootElement.GetProperty("lines")[0].GetProperty("unitOfMeasureCode").GetString().Should().Be("EA");
            snapshot.RootElement.GetProperty("lines")[0].GetProperty("grossAmount").GetDecimal().Should().Be(8m);
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
    public async Task Material_amendment_requires_reapproval_and_status_only_update_is_rejected()
    {
        fixture.EnsureEnabled();
        var tenantId = $"po-approval-{Guid.NewGuid():N}";
        await using var context = fixture.CreateContext(tenantId);

        var unit = new UnitOfMeasure
        {
            ExternalId = $"UOM-{Guid.NewGuid():N}",
            Code = "EA",
            Name = "Each",
            DecimalPlaces = 0,
            IsWholeUnitOnly = true
        };
        var supplier = new Supplier { Name = "Approved supplier", Address = "Dock 1", Email = "vendor@example.test" };
        context.UnitsOfMeasure.Add(unit);
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var item = new Item
        {
            ItemCode = $"SKU-{Guid.NewGuid():N}"[..20],
            Description = "Purchase-order approval test item",
            Rate = 4m,
            BaseUnitId = unit.Id,
            PurchaseUnitId = unit.Id,
            PurchaseToBaseFactor = 1m,
            QuantityPrecision = 0,
            WholeUnitOnly = true
        };
        context.Items.Add(item);
        await context.SaveChangesAsync();

        var service = CreateService(context, tenantId);
        var order = await service.CreateAsync(
            new PurchaseOrder
            {
                PONumber = $"PO-{Guid.NewGuid():N}"[..20],
                SupplierId = supplier.Id,
                CurrencyScale = 2,
                DeliveryTerms = "Deliver to Dock 1"
            },
            [new OrderDetail { ItemId = item.Id, Quantity = 2, UnitPrice = 4m }],
            Guid.NewGuid().ToString("N"));

        await service.UpdateStatusAsync(order.Id, nameof(PurchaseOrderStatus.Approved));
        order.ApprovedCommercialVersion.Should().Be(1);
        order.ApprovedCommercialSnapshotJson.Should().Contain("\"unitOfMeasureCode\":\"EA\"");

        var line = order.OrderDetails.Single();
        await service.AmendApprovedAsync(order.Id, new PurchaseOrderAmendment(
            ExpectedCommercialVersion: 1,
            SupplierId: supplier.Id,
            DeliveryTerms: "Deliver to Dock 2",
            Notes: null,
            CurrencyScale: 2,
            Lines: [new PurchaseOrderAmendmentLine(
                line.Id, item.Id, 2, 6m, 0m, null, 0m,
                TaxCategory.Standard, TaxCalculationMode.Exclusive, DocumentLineDirection.Charge)]));

        context.ChangeTracker.Clear();
        var amended = await context.PurchaseOrders.SingleAsync(po => po.Id == order.Id);
        amended.Status.Should().Be(PurchaseOrderStatus.Pending);
        amended.CommercialVersion.Should().Be(2);
        amended.ApprovedCommercialVersion.Should().Be(1);
        amended.DeliveryTerms.Should().Be("Deliver to Dock 2");
        amended.ApprovedCommercialSnapshotJson.Should().Contain("Deliver to Dock 1");

        Func<Task> bypass = async () =>
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"PurchaseOrders\" SET \"Status\" = 'Approved' WHERE \"Id\" = {order.Id}");
        };
        var constraintFailure = await FluentActions.Awaiting(bypass).Should().ThrowAsync<PostgresException>();
        constraintFailure.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);

        await service.UpdateStatusAsync(order.Id, nameof(PurchaseOrderStatus.Approved));
        context.ChangeTracker.Clear();
        var reapproved = await context.PurchaseOrders.SingleAsync(po => po.Id == order.Id);
        reapproved.Status.Should().Be(PurchaseOrderStatus.Approved);
        reapproved.CommercialVersion.Should().Be(2);
        reapproved.ApprovedCommercialVersion.Should().Be(2);
        reapproved.ApprovedCommercialSnapshotJson.Should().Contain("Deliver to Dock 2");
    }

    private static PurchaseOrderService CreateService(InventoryDbContext context, string tenantId)
    {
        var unitOfWork = new UnitOfWork(context);
        var documentService = new DocumentIdentityService(
            context, unitOfWork, new DocumentNumberService(context, unitOfWork));
        var webhooks = new WebhookDispatcher(
            new ServiceCollection().BuildServiceProvider(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<WebhookDispatcher>.Instance,
            context);

        return new PurchaseOrderService(
            new Repository<PurchaseOrder>(context),
            unitOfWork,
            documentService,
            webhooks,
            new TestTenantContext(tenantId),
            NullLogger<PurchaseOrderService>.Instance,
            new Repository<TaxRule>(context),
            new Repository<OrderDetail>(context),
            new Repository<Supplier>(context),
            new Repository<Item>(context),
            new Repository<UnitOfMeasure>(context),
            new Repository<DocumentIdentity>(context));
    }
}
