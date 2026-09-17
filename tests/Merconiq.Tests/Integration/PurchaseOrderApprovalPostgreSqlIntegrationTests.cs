using FluentAssertions;
using System.Data.Common;
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
            snapshot.RootElement.GetProperty("orderDate").GetDateTime().Should().Be(upgraded.OrderDate);
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
    public async Task Approval_migration_fails_atomically_when_an_approved_order_has_no_lines()
    {
        fixture.EnsureEnabled();
        const string migrationId = "20260918010000_AddPurchaseOrderApprovalVersioning";
        var schema = $"po_approval_empty_{Guid.NewGuid():N}";
        var tenantId = $"po-approval-empty-{Guid.NewGuid():N}";
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

            var supplier = new Supplier { Name = "Legacy supplier without PO lines" };
            var number = $"PO-EMPTY-{Guid.NewGuid():N}"[..20];
            var document = DocumentIdentity.Create(
                DocumentIdentityId.New(), tenantId, null, "PurchaseOrder", number, 2026,
                DocumentLifecycleStatus.Active, "PurchaseOrderApprovalEmptyBackfillTest");
            context.Suppliers.Add(supplier);
            context.DocumentIdentities.Add(document);
            await context.SaveChangesAsync();

            await using (var connection = new NpgsqlConnection(connectionString.ConnectionString))
            {
                await connection.OpenAsync();
                await using var insertOrder = new NpgsqlCommand(
                    """
                    INSERT INTO "PurchaseOrders" (
                        "PONumber", "OrderDate", "SupplierId", "TotalAmount", "NetAmount",
                        "DiscountAmount", "TaxAmount", "CurrencyScale", "CalculationVersion",
                        "Status", "Notes", "TenantId", "CreatedAt", "DocumentId")
                    VALUES (@number, @date, @supplier, 0, 0, 0, 0, 2, 1,
                        'Approved', NULL, @tenant, now(), @document)
                    """,
                    connection);
                insertOrder.Parameters.AddWithValue("number", number);
                insertOrder.Parameters.AddWithValue("date", new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc));
                insertOrder.Parameters.AddWithValue("supplier", supplier.Id);
                insertOrder.Parameters.AddWithValue("tenant", tenantId);
                insertOrder.Parameters.AddWithValue("document", document.Id.Value);
                await insertOrder.ExecuteNonQueryAsync();
            }

            var failure = await FluentActions.Awaiting(() => migrator.MigrateAsync(migrationId))
                .Should().ThrowAsync<PostgresException>();
            failure.Which.MessageText.Should().Contain(
                "Approval snapshot backfill blocked: an approved purchase order has no lines.");

            await using var verification = new NpgsqlConnection(connectionString.ConnectionString);
            await verification.OpenAsync();
            await using var columns = new NpgsqlCommand(
                """
                SELECT COUNT(*)
                FROM information_schema.columns
                WHERE table_schema = @schema
                  AND table_name = 'PurchaseOrders'
                  AND column_name IN (
                      'ApprovedCommercialSnapshotJson', 'ApprovedCommercialVersion',
                      'CommercialVersion', 'DeliveryTerms')
                """,
                verification);
            columns.Parameters.AddWithValue("schema", schema);
            ((long)(await columns.ExecuteScalarAsync())!).Should().Be(0,
                "the failed migration must roll back its column additions");

            await using var history = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = @id)",
                verification);
            history.Parameters.AddWithValue("id", migrationId);
            ((bool)(await history.ExecuteScalarAsync())!).Should().BeFalse(
                "a failed migration must not be marked as applied");
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
    public async Task Approval_retry_after_ambiguous_commit_reloads_purchase_order_state()
    {
        fixture.EnsureEnabled();
        var tenantId = $"po-approval-retry-{Guid.NewGuid():N}";
        var commitInterceptor = new ThrowOnceAfterCommitInterceptor();
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(fixture.ConnectionString, postgres => postgres.EnableRetryOnFailure(3))
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(commitInterceptor)
            .Options;
        await using var context = new InventoryDbContext(options, new TestTenantContext(tenantId));

        var unit = new UnitOfMeasure { Code = "EA", Name = "Each", DecimalPlaces = 0, IsWholeUnitOnly = true };
        var supplier = new Supplier { Name = "Retry supplier" };
        context.UnitsOfMeasure.Add(unit);
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var item = new Item
        {
            ItemCode = $"RETRY-{Guid.NewGuid():N}"[..20], Description = "Approval retry item", Rate = 1m,
            BaseUnitId = unit.Id, PurchaseUnitId = unit.Id, PurchaseToBaseFactor = 1m,
            QuantityPrecision = 0, WholeUnitOnly = true
        };
        context.Items.Add(item);
        await context.SaveChangesAsync();

        var service = CreateService(context, tenantId);
        var order = await service.CreateAsync(
            new PurchaseOrder
            {
                PONumber = $"PO-RETRY-{Guid.NewGuid():N}"[..32],
                SupplierId = supplier.Id,
                CurrencyScale = 2
            },
            [new OrderDetail { ItemId = item.Id, Quantity = 2, UnitPrice = 4m }],
            Guid.NewGuid().ToString("N"));

        commitInterceptor.Arm();
        await service.UpdateStatusAsync(order.Id, nameof(PurchaseOrderStatus.Approved));

        commitInterceptor.CommitCallbacksAfterArm.Should().Be(2,
            "the first successful database commit reports a transient client-side error and must be retried");
        await using var verification = fixture.CreateContext(tenantId);
        var persistedOrder = await verification.PurchaseOrders.AsNoTracking().SingleAsync(po => po.Id == order.Id);
        persistedOrder.Status.Should().Be(PurchaseOrderStatus.Approved);
        persistedOrder.ApprovedCommercialVersion.Should().Be(persistedOrder.CommercialVersion);
        persistedOrder.ApprovedCommercialSnapshotJson.Should().NotBeNullOrWhiteSpace();
    }

    [PostgreSqlFact]
    public async Task Approved_purchase_order_amendment_round_trips_four_decimal_unit_price()
    {
        fixture.EnsureEnabled();
        var tenantId = $"po-approval-price-{Guid.NewGuid():N}";
        await using var context = fixture.CreateContext(tenantId);
        var unit = new UnitOfMeasure { Code = "EA", Name = "Each", DecimalPlaces = 0, IsWholeUnitOnly = true };
        var supplier = new Supplier { Name = "Precision supplier" };
        context.UnitsOfMeasure.Add(unit);
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var item = new Item
        {
            ItemCode = $"PREC-{Guid.NewGuid():N}"[..20], Description = "Precision test item", Rate = 1m,
            BaseUnitId = unit.Id, PurchaseUnitId = unit.Id, PurchaseToBaseFactor = 1m,
            QuantityPrecision = 0, WholeUnitOnly = true
        };
        context.Items.Add(item);
        await context.SaveChangesAsync();

        var service = CreateService(context, tenantId);
        var order = await service.CreateAsync(
            new PurchaseOrder
            {
                PONumber = $"PO-PRICE-{Guid.NewGuid():N}"[..32],
                SupplierId = supplier.Id,
                CurrencyScale = 4
            },
            [new OrderDetail { ItemId = item.Id, Quantity = 3, UnitPrice = 1m }],
            Guid.NewGuid().ToString("N"));
        await service.UpdateStatusAsync(order.Id, nameof(PurchaseOrderStatus.Approved));

        var form = await service.GetForAmendmentAsync(order.Id);
        var line = form!.OrderDetails.Single();
        await service.AmendApprovedAsync(order.Id, new PurchaseOrderAmendment(
            form.CommercialVersion,
            supplier.Id,
            "Precision-checked delivery",
            null,
            4,
            [new PurchaseOrderAmendmentLine(
                line.Id, item.Id, 3, 1.2345m, 0m, null, 0m,
                TaxCategory.Standard, TaxCalculationMode.Exclusive, DocumentLineDirection.Charge)]));

        context.ChangeTracker.Clear();
        var storedOrder = await context.PurchaseOrders.SingleAsync(candidate => candidate.Id == order.Id);
        var storedLine = await context.OrderDetails.SingleAsync(candidate => candidate.PurchaseOrderId == order.Id);
        storedLine.UnitPrice.Should().Be(1.2345m);
        storedLine.GrossAmount.Should().Be(3.7035m);
        storedOrder.TotalAmount.Should().Be(3.7035m);
    }

    [PostgreSqlFact]
    public async Task Unit_price_precision_migration_refuses_to_round_values_on_downgrade()
    {
        fixture.EnsureEnabled();
        const string precisionMigrationId = "20260918015000_ExpandPurchaseOrderUnitPricePrecision";
        var tenantId = $"po-approval-price-down-{Guid.NewGuid():N}";
        var schema = $"po_price_down_{Guid.NewGuid():N}";
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
            await context.Database.MigrateAsync();

            var unit = new UnitOfMeasure { Code = "EA", Name = "Each", DecimalPlaces = 0, IsWholeUnitOnly = true };
            var supplier = new Supplier { Name = "Precision downgrade supplier" };
            context.UnitsOfMeasure.Add(unit);
            context.Suppliers.Add(supplier);
            await context.SaveChangesAsync();
            var item = new Item
            {
                ItemCode = $"DOWN-{Guid.NewGuid():N}"[..20], Description = "Precision downgrade item", Rate = 1m,
                BaseUnitId = unit.Id, PurchaseUnitId = unit.Id, PurchaseToBaseFactor = 1m,
                QuantityPrecision = 0, WholeUnitOnly = true
            };
            context.Items.Add(item);
            await context.SaveChangesAsync();

            var service = CreateService(context, tenantId);
            var order = await service.CreateAsync(
                new PurchaseOrder
                {
                    PONumber = $"PO-DOWN-{Guid.NewGuid():N}"[..32],
                    SupplierId = supplier.Id,
                    CurrencyScale = 4
                },
                [new OrderDetail { ItemId = item.Id, Quantity = 1, UnitPrice = 1.2345m }],
                Guid.NewGuid().ToString("N"));
            context.ChangeTracker.Clear();

            var failure = await FluentActions.Awaiting(() => context.GetService<IMigrator>().MigrateAsync(
                    "20260918010000_AddPurchaseOrderApprovalVersioning"))
                .Should().ThrowAsync<PostgresException>();
            failure.Which.MessageText.Should().Contain(
                "Cannot downgrade UnitPrice precision while stored values require decimal(20,4).");

            var storedLine = await context.OrderDetails.SingleAsync(line => line.PurchaseOrderId == order.Id);
            storedLine.UnitPrice.Should().Be(1.2345m);
            (await context.Database.GetAppliedMigrationsAsync()).Should().Contain(precisionMigrationId);
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
    public async Task Amendment_preserves_tax_snapshot_when_the_original_rule_has_expired()
    {
        fixture.EnsureEnabled();
        var tenantId = $"po-approval-expired-tax-{Guid.NewGuid():N}";
        await using var context = fixture.CreateContext(tenantId);
        var unit = new UnitOfMeasure { Code = "EA", Name = "Each", DecimalPlaces = 0, IsWholeUnitOnly = true };
        var supplier = new Supplier { Name = "Historical tax supplier" };
        context.UnitsOfMeasure.Add(unit);
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var item = new Item
        {
            ItemCode = $"TAX-{Guid.NewGuid():N}"[..20], Description = "Historical tax item", Rate = 100m,
            BaseUnitId = unit.Id, PurchaseUnitId = unit.Id, PurchaseToBaseFactor = 1m,
            QuantityPrecision = 0, WholeUnitOnly = true
        };
        var rule = new TaxRule
        {
            Code = $"TAX-{Guid.NewGuid():N}"[..20],
            Category = TaxCategory.Standard,
            RatePercent = 7.5m,
            CalculationMode = TaxCalculationMode.Inclusive,
            EffectiveFromUtc = DateTime.UtcNow.AddDays(-2),
            EffectiveToUtc = DateTime.UtcNow.AddDays(1),
            IsActive = true
        };
        context.Items.Add(item);
        context.TaxRules.Add(rule);
        await context.SaveChangesAsync();

        var service = CreateService(context, tenantId);
        var order = await service.CreateAsync(
            new PurchaseOrder
            {
                PONumber = $"PO-TAX-{Guid.NewGuid():N}"[..32],
                SupplierId = supplier.Id,
                CurrencyScale = 2
            },
            [new OrderDetail { ItemId = item.Id, Quantity = 1, UnitPrice = 100m, TaxRuleId = rule.Id }],
            Guid.NewGuid().ToString("N"));
        await service.UpdateStatusAsync(order.Id, nameof(PurchaseOrderStatus.Approved));

        var originalLine = await context.OrderDetails.SingleAsync(line => line.PurchaseOrderId == order.Id);
        var originalRate = originalLine.TaxRatePercent;
        var originalCategory = originalLine.TaxCategory;
        var originalMode = originalLine.TaxMode;
        var originalEffectiveFrom = originalLine.TaxEffectiveFromUtc;
        rule.IsActive = false;
        rule.EffectiveToUtc = DateTime.UtcNow.AddMinutes(-1);
        await context.SaveChangesAsync();

        var form = await service.GetForAmendmentAsync(order.Id);
        var line = form!.OrderDetails.Single();
        await service.AmendApprovedAsync(order.Id, new PurchaseOrderAmendment(
            form.CommercialVersion,
            supplier.Id,
            "New delivery bay, same tax terms",
            null,
            form.CurrencyScale,
            [new PurchaseOrderAmendmentLine(
                line.Id, item.Id, line.Quantity, line.UnitPrice, line.DiscountPercent,
                line.TaxRuleId, line.TaxRatePercent, line.TaxCategory, line.TaxMode, line.Direction)]));

        context.ChangeTracker.Clear();
        var amendedLine = await context.OrderDetails.SingleAsync(candidate => candidate.PurchaseOrderId == order.Id);
        amendedLine.TaxRuleId.Should().Be(rule.Id);
        amendedLine.TaxRatePercent.Should().Be(originalRate);
        amendedLine.TaxCategory.Should().Be(originalCategory);
        amendedLine.TaxMode.Should().Be(originalMode);
        amendedLine.TaxEffectiveFromUtc.Should().Be(originalEffectiveFrom);
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
        using (var approvedSnapshot = System.Text.Json.JsonDocument.Parse(order.ApprovedCommercialSnapshotJson!))
        {
            approvedSnapshot.RootElement.GetProperty("orderDate").GetDateTime().Should().Be(order.OrderDate);
        }

        // Exercise the amendment page's read-then-save sequence on one scoped context.
        var amendmentForm = await service.GetForAmendmentAsync(order.Id);
        amendmentForm.Should().NotBeNull();
        context.Entry(amendmentForm!).State.Should().Be(EntityState.Detached);
        var line = amendmentForm!.OrderDetails.Single();
        context.Entry(line).State.Should().Be(EntityState.Detached);
        await service.AmendApprovedAsync(order.Id, new PurchaseOrderAmendment(
            ExpectedCommercialVersion: amendmentForm.CommercialVersion,
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

    [PostgreSqlFact]
    public async Task Approval_migration_downgrade_holds_table_lock_through_preflight_and_column_drops()
    {
        fixture.EnsureEnabled();
        var schema = $"po_approval_down_{Guid.NewGuid():N}";
        var tenantId = $"po-approval-down-{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { SearchPath = schema };
        await using (var createSchema = new NpgsqlConnection(fixture.ConnectionString))
        {
            await createSchema.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", createSchema);
            await command.ExecuteNonQueryAsync();
        }

        var lockPause = new PauseAfterCommandInterceptor("LOCK TABLE \"PurchaseOrders\" IN ACCESS EXCLUSIVE MODE");
        Task? downgradeTask = null;
        try
        {
            var setupOptions = new DbContextOptionsBuilder<InventoryDbContext>()
                .UseNpgsql(connectionString.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            int purchaseOrderId;
            await using (var setup = new InventoryDbContext(setupOptions, new TestTenantContext(tenantId)))
            {
                await setup.Database.MigrateAsync();
                var supplier = new Supplier { Name = "Downgrade lock supplier" };
                var item = new Item { ItemCode = $"DOWN-{Guid.NewGuid():N}"[..20], Description = "Lock test item", Rate = 1m };
                setup.Suppliers.Add(supplier);
                setup.Items.Add(item);
                await setup.SaveChangesAsync();

                var service = CreateService(setup, tenantId);
                var order = await service.CreateAsync(
                    new PurchaseOrder
                    {
                        PONumber = $"PO-DOWN-{Guid.NewGuid():N}"[..32],
                        SupplierId = supplier.Id,
                        CurrencyScale = 2
                    },
                    [new OrderDetail { ItemId = item.Id, Quantity = 1, UnitPrice = 1m }],
                    Guid.NewGuid().ToString("N"));
                purchaseOrderId = order.Id;
            }

            var migrationOptions = new DbContextOptionsBuilder<InventoryDbContext>()
                .UseNpgsql(connectionString.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .AddInterceptors(lockPause)
                .Options;
            await using var migrationContext = new InventoryDbContext(migrationOptions, new TestTenantContext(tenantId));
            lockPause.Arm();
            downgradeTask = migrationContext.GetService<IMigrator>()
                .MigrateAsync("20260918000000_AddMasterDataExternalIds");
            await lockPause.Paused.WaitAsync(TimeSpan.FromSeconds(15));

            await using var writerConnection = new NpgsqlConnection(connectionString.ConnectionString);
            await writerConnection.OpenAsync();
            await using var approval = new NpgsqlCommand(
                """
                UPDATE "PurchaseOrders"
                SET "Status" = 'Approved',
                    "ApprovedCommercialVersion" = "CommercialVersion",
                    "ApprovedCommercialSnapshotJson" = '{}'::jsonb
                WHERE "Id" = @id
                """,
                writerConnection);
            approval.Parameters.AddWithValue("id", purchaseOrderId);
            var approvalTask = approval.ExecuteNonQueryAsync();
            var completedBeforeUnlock = await Task.WhenAny(approvalTask, Task.Delay(250));
            completedBeforeUnlock.Should().NotBeSameAs(approvalTask,
                "an approval write must wait while the downgrade holds its table lock");

            lockPause.Release();
            await downgradeTask.WaitAsync(TimeSpan.FromSeconds(15));
            Func<Task> waitForApproval = async () => await approvalTask;
            var missingColumn = await FluentActions.Awaiting(waitForApproval).Should().ThrowAsync<PostgresException>();
            missingColumn.Which.SqlState.Should().Be(PostgresErrorCodes.UndefinedColumn);
        }
        finally
        {
            lockPause.Release();
            if (downgradeTask is not null)
            {
                try { await downgradeTask; }
                catch { /* Preserve the originating assertion or migration error. */ }
            }

            await using var dropSchema = new NpgsqlConnection(fixture.ConnectionString);
            await dropSchema.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", dropSchema);
            await command.ExecuteNonQueryAsync();
        }
    }

    [PostgreSqlFact]
    public async Task Approval_snapshot_uses_one_repeatable_read_across_concurrent_master_edits()
    {
        fixture.EnsureEnabled();
        var tenantId = $"po-approval-snapshot-{Guid.NewGuid():N}";
        var pause = new PauseAfterCommandInterceptor("FROM \"OrderDetails\"");
        await using var context = fixture.CreateContext(tenantId, interceptors: [pause]);
        var unit = new UnitOfMeasure
        {
            Code = "EA",
            Name = "Each",
            DecimalPlaces = 0,
            IsWholeUnitOnly = true
        };
        var supplier = new Supplier { Name = "Supplier before concurrent edit" };
        context.UnitsOfMeasure.Add(unit);
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var item = new Item
        {
            ItemCode = $"SNAP-{Guid.NewGuid():N}"[..20],
            Description = "Item before concurrent edit",
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
                PONumber = $"PO-SNAP-{Guid.NewGuid():N}"[..32],
                SupplierId = supplier.Id,
                CurrencyScale = 2
            },
            [new OrderDetail { ItemId = item.Id, Quantity = 2, UnitPrice = 4m }],
            Guid.NewGuid().ToString("N"));

        pause.Arm();
        Task? approvalTask = null;
        try
        {
            approvalTask = service.UpdateStatusAsync(order.Id, nameof(PurchaseOrderStatus.Approved));
            await pause.Paused.WaitAsync(TimeSpan.FromSeconds(15));

            await using (var editor = fixture.CreateContext(tenantId))
            {
                var editedSupplier = await editor.Suppliers.SingleAsync(candidate => candidate.Id == supplier.Id);
                var editedItem = await editor.Items.SingleAsync(candidate => candidate.Id == item.Id);
                var editedUnit = await editor.UnitsOfMeasure.SingleAsync(candidate => candidate.Id == unit.Id);
                editedSupplier.Name = "Supplier after concurrent edit";
                editedItem.Description = "Item after concurrent edit";
                editedUnit.Code = "EA-CHANGED";
                await editor.SaveChangesAsync();
            }

            pause.Release();
            await approvalTask.WaitAsync(TimeSpan.FromSeconds(15));

            using var snapshot = System.Text.Json.JsonDocument.Parse(order.ApprovedCommercialSnapshotJson!);
            snapshot.RootElement.GetProperty("supplierName").GetString().Should().Be("Supplier before concurrent edit");
            snapshot.RootElement.GetProperty("lines")[0].GetProperty("itemDescription").GetString()
                .Should().Be("Item before concurrent edit");
            snapshot.RootElement.GetProperty("lines")[0].GetProperty("unitOfMeasureCode").GetString().Should().Be("EA");
        }
        finally
        {
            pause.Release();
            if (approvalTask is not null)
            {
                try { await approvalTask; }
                catch { /* Preserve the originating assertion or approval error. */ }
            }
        }
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

    private sealed class PauseAfterCommandInterceptor(string commandFragment) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _pauseStarted;

        public Task Paused => _paused.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.TrySetResult(true);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            await PauseIfMatchedAsync(command.CommandText, cancellationToken);
            return result;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            await PauseIfMatchedAsync(command.CommandText, cancellationToken);
            return result;
        }

        private async Task PauseIfMatchedAsync(string commandText, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _armed) == 0 ||
                !commandText.Contains(commandFragment, StringComparison.OrdinalIgnoreCase) ||
                Interlocked.Exchange(ref _pauseStarted, 1) != 0)
                return;

            _paused.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ThrowOnceAfterCommitInterceptor : DbTransactionInterceptor
    {
        private int _armed;
        private int _failureInjected;
        private int _commitCallbacksAfterArm;

        public int CommitCallbacksAfterArm => Volatile.Read(ref _commitCallbacksAfterArm);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 0)
            {
                return Task.CompletedTask;
            }

            Interlocked.Increment(ref _commitCallbacksAfterArm);
            if (Interlocked.Exchange(ref _failureInjected, 1) == 0)
            {
                throw new NpgsqlException(
                    "Simulated transient connection loss after PostgreSQL committed the transaction.",
                    new IOException("Simulated lost commit acknowledgement."));
            }

            return Task.CompletedTask;
        }
    }
}
