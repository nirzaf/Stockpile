using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Moq;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class DocumentIdentityPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Concurrent_retries_allocate_one_number_and_reject_a_changed_request()
    {
        fixture.EnsureEnabled();
        var tenantId = $"document-identity-race-{Guid.NewGuid():N}";
        int companyId;
        await using (var setup = fixture.CreateContext(tenantId))
        {
            var company = new Company { Code = $"DI-{Guid.NewGuid():N}"[..15], LegalName = "Identity test company" };
            setup.Companies.Add(company);
            await setup.SaveChangesAsync();
            companyId = company.Id;
        }

        const int retries = 16;
        var requestKey = Guid.NewGuid().ToString("N");
        var requestHash = Hash("same invoice request");
        var startGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, retries).Select(async _ =>
        {
            await startGate.Task;
            await using var context = fixture.CreateContext(tenantId);
            var unitOfWork = new UnitOfWork(context);
            var service = new DocumentIdentityService(context, unitOfWork, new DocumentNumberService(context, unitOfWork));
            return await service.CreateNumberedAsync(
                companyId,
                "PurchaseInvoice",
                2026,
                "PINV-",
                "PurchaseInvoice.Create",
                requestKey,
                requestHash);
        }).ToArray();

        startGate.SetResult(true);
        var identities = await Task.WhenAll(attempts);
        identities.Select(identity => identity.Id).Distinct().Should().ContainSingle();
        identities.Select(identity => identity.HumanNumber).Distinct().Should().ContainSingle();

        await using (var verify = fixture.CreateContext(tenantId))
        {
            (await verify.DocumentIdentities.CountAsync()).Should().Be(1);
            (await verify.DocumentNumberSequences.SingleAsync()).NextNumber.Should().Be(2);
        }

        await using (var retryContext = fixture.CreateContext(tenantId))
        {
            var unitOfWork = new UnitOfWork(retryContext);
            var service = new DocumentIdentityService(retryContext, unitOfWork, new DocumentNumberService(retryContext, unitOfWork));
            var act = () => service.CreateNumberedAsync(
                companyId,
                "PurchaseInvoice",
                2026,
                "PINV-",
                "PurchaseInvoice.Create",
                requestKey,
                Hash("different invoice request"));

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("The idempotency key was already used with a different document request.");

            var changedSeries = () => service.CreateNumberedAsync(
                companyId,
                "PurchaseInvoice",
                2026,
                "OTHER-",
                "PurchaseInvoice.Create",
                requestKey,
                requestHash);
            await changedSeries.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("The idempotency key was already used with a different document request.");
        }
    }

    [PostgreSqlFact]
    public async Task Company_scoped_human_number_uniqueness_includes_the_period()
    {
        fixture.EnsureEnabled();
        var tenantId = $"document-identity-period-{Guid.NewGuid():N}";
        await using var context = fixture.CreateContext(tenantId);
        var company = new Company { Code = $"DP-{Guid.NewGuid():N}"[..15], LegalName = "Period identity test company" };
        context.Companies.Add(company);
        await context.SaveChangesAsync();

        var priorPeriod = CreateDocument(tenantId, company.Id, "INV-000001", period: 2025);
        var currentPeriod = CreateDocument(tenantId, company.Id, "INV-000001", period: 2026);
        context.DocumentIdentities.AddRange(priorPeriod, currentPeriod);
        await context.SaveChangesAsync();

        var duplicateInCurrentPeriod = CreateDocument(tenantId, company.Id, "INV-000001", period: 2026);
        context.DocumentIdentities.Add(duplicateInCurrentPeriod);
        var act = () => context.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [PostgreSqlFact]
    public async Task Purchase_order_retry_returns_the_original_identity_and_preserves_its_number()
    {
        fixture.EnsureEnabled();
        var tenantId = $"purchase-order-identity-{Guid.NewGuid():N}";
        int supplierId;
        int itemId;
        await using (var setup = fixture.CreateContext(tenantId))
        {
            var supplier = new Supplier { Name = "Identity supplier" };
            var item = new Item { ItemCode = "IDENTITY-ITEM", Description = "Identity test item", Rate = 3m };
            setup.Suppliers.Add(supplier);
            setup.Items.Add(item);
            await setup.SaveChangesAsync();
            supplierId = supplier.Id;
            itemId = item.Id;
        }

        var requestKey = Guid.NewGuid().ToString("N");
        var first = await CreatePurchaseOrderAsync(tenantId, supplierId, itemId, requestKey, notes: "deliver to receiving");
        var replay = await CreatePurchaseOrderAsync(tenantId, supplierId, itemId, requestKey, notes: "deliver to receiving");

        replay.Id.Should().Be(first.Id);
        replay.DocumentId.Should().Be(first.DocumentId);
        replay.PONumber.Should().Be("LEGACY-PO-0042");

        await using (var verify = fixture.CreateContext(tenantId))
        {
            (await verify.PurchaseOrders.CountAsync()).Should().Be(1);
            var identity = await verify.DocumentIdentities.SingleAsync();
            identity.Id.Should().Be(first.DocumentId);
            identity.HumanNumber.Should().Be("LEGACY-PO-0042");
            identity.CompanyId.Should().BeNull("legacy purchase-order ownership is not known");
            (await verify.DocumentLineIdentities.CountAsync(line => line.DocumentId == first.DocumentId)).Should().Be(1);
        }

        await FluentActions.Invoking(() => CreatePurchaseOrderAsync(
                tenantId,
                supplierId,
                itemId,
                requestKey,
                notes: "different request"))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The idempotency key was already used with a different document request.");
    }

    [PostgreSqlFact]
    public async Task Line_links_require_company_mapping_and_reject_cross_company_sources()
    {
        fixture.EnsureEnabled();
        var tenantId = $"document-line-links-{Guid.NewGuid():N}";
        DocumentLineIdentityId companyOneLineA;
        DocumentLineIdentityId companyOneLineB;
        DocumentLineIdentityId companyTwoLine;
        DocumentLineIdentityId unassignedLine;
        DocumentLineIdentityId mismatchedCompanyLine;
        await using (var setup = fixture.CreateContext(tenantId))
        {
            var companyOne = new Company { Code = $"L1-{Guid.NewGuid():N}"[..14], LegalName = "Company one" };
            var companyTwo = new Company { Code = $"L2-{Guid.NewGuid():N}"[..14], LegalName = "Company two" };
            setup.Companies.AddRange(companyOne, companyTwo);
            await setup.SaveChangesAsync();

            var docA = CreateDocument(tenantId, companyOne.Id, "PO-LINK-A");
            var docB = CreateDocument(tenantId, companyOne.Id, "PO-LINK-B");
            var docOtherCompany = CreateDocument(tenantId, companyTwo.Id, "PO-LINK-C");
            var docUnassigned = CreateDocument(tenantId, null, "PO-LINK-U");
            var docMismatchedLine = CreateDocument(tenantId, companyOne.Id, "PO-LINK-M");
            var lineA = CreateLine(tenantId, companyOne.Id, docA);
            var lineB = CreateLine(tenantId, companyOne.Id, docB);
            var lineOtherCompany = CreateLine(tenantId, companyTwo.Id, docOtherCompany);
            var lineUnassigned = CreateLine(tenantId, null, docUnassigned);
            var lineCompanyMismatch = CreateLine(tenantId, companyTwo.Id, docMismatchedLine);
            setup.DocumentIdentities.AddRange(docA, docB, docOtherCompany, docUnassigned, docMismatchedLine);
            setup.DocumentLineIdentities.AddRange(lineA, lineB, lineOtherCompany, lineUnassigned, lineCompanyMismatch);
            await setup.SaveChangesAsync();
            companyOneLineA = lineA.Id;
            companyOneLineB = lineB.Id;
            companyTwoLine = lineOtherCompany.Id;
            unassignedLine = lineUnassigned.Id;
            mismatchedCompanyLine = lineCompanyMismatch.Id;
        }

        await using var context = fixture.CreateContext(tenantId);
        var unitOfWork = new UnitOfWork(context);
        var service = new DocumentIdentityService(context, unitOfWork, new DocumentNumberService(context, unitOfWork));
        await service.LinkLinesAsync(companyOneLineA, companyOneLineB, DocumentLineRelationshipType.Successor);

        var crossCompany = () => service.LinkLinesAsync(companyOneLineA, companyTwoLine, DocumentLineRelationshipType.Source);
        await crossCompany.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Cross-company document-line links are not allowed.");

        var legacyUnassigned = () => service.LinkLinesAsync(companyOneLineA, unassignedLine, DocumentLineRelationshipType.Source);
        await legacyUnassigned.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Document lines must be mapped to a company before lineage can be recorded.");

        var mismatchedCompany = () => service.LinkLinesAsync(
            companyOneLineA,
            mismatchedCompanyLine,
            DocumentLineRelationshipType.Source);
        await mismatchedCompany.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Document-line company mapping must match its owning document.");

        (await context.DocumentLineLinks.CountAsync()).Should().Be(1);
    }

    [PostgreSqlFact]
    public async Task Purchase_order_amount_snapshots_survive_a_tax_rule_change()
    {
        fixture.EnsureEnabled();
        var tenantId = $"tax-snapshot-{Guid.NewGuid():N}";
        await using var context = fixture.CreateContext(tenantId);
        var supplier = new Supplier { Name = "Tax snapshot supplier" };
        var item = new Item { ItemCode = "TAX-SNAPSHOT-ITEM", Description = "Tax snapshot item", Rate = 100m };
        var rule = new TaxRule
        {
            Code = "STANDARD-15",
            Category = TaxCategory.Standard,
            RatePercent = 15m,
            CalculationMode = TaxCalculationMode.Exclusive,
            EffectiveFromUtc = DateTime.UtcNow.AddDays(-1),
            IsActive = true
        };
        context.Suppliers.Add(supplier);
        context.Items.Add(item);
        context.TaxRules.Add(rule);
        await context.SaveChangesAsync();

        var unitOfWork = new UnitOfWork(context);
        var purchaseOrders = new Repository<PurchaseOrder>(context);
        var taxRules = new Repository<TaxRule>(context);
        var service = new PurchaseOrderService(
            purchaseOrders,
            unitOfWork,
            new DocumentIdentityService(context, unitOfWork, new DocumentNumberService(context, unitOfWork)),
            new Mock<IWebhookDispatcher>().Object,
            new TestTenantContext(tenantId),
            NullLogger<PurchaseOrderService>.Instance,
            taxRules);
        var order = new PurchaseOrder
        {
            PONumber = "TAX-SNAPSHOT-001",
            SupplierId = supplier.Id,
            CurrencyScale = 2
        };
        var detail = new OrderDetail
        {
            ItemId = item.Id,
            Quantity = 1,
            UnitPrice = 100m,
            TaxRuleId = rule.Id
        };

        var requestKey = Guid.NewGuid().ToString("N");
        var created = await service.CreateAsync(order, [detail], requestKey);
        created.TotalAmount.Should().Be(115m);
        created.TaxAmount.Should().Be(15m);
        detail.TaxRatePercent.Should().Be(15m);

        var storedRule = await context.TaxRules.SingleAsync(value => value.Id == rule.Id);
        storedRule.RatePercent = 20m;
        storedRule.EffectiveToUtc = DateTime.UtcNow.AddSeconds(-1);
        await context.SaveChangesAsync();

        await using (var replayContext = fixture.CreateContext(tenantId))
        {
            var replayUnitOfWork = new UnitOfWork(replayContext);
            var replayService = new PurchaseOrderService(
                new Repository<PurchaseOrder>(replayContext),
                replayUnitOfWork,
                new DocumentIdentityService(
                    replayContext,
                    replayUnitOfWork,
                    new DocumentNumberService(replayContext, replayUnitOfWork)),
                new Mock<IWebhookDispatcher>().Object,
                new TestTenantContext(tenantId),
                NullLogger<PurchaseOrderService>.Instance,
                new Repository<TaxRule>(replayContext));
            var replay = await replayService.CreateAsync(
                new PurchaseOrder
                {
                    PONumber = "TAX-SNAPSHOT-001",
                    SupplierId = supplier.Id,
                    CurrencyScale = 2
                },
                [new OrderDetail { ItemId = item.Id, Quantity = 1, UnitPrice = 100m, TaxRuleId = rule.Id }],
                requestKey);
            replay.Id.Should().Be(created.Id);
            replay.TotalAmount.Should().Be(115m);
        }

        await using var verify = fixture.CreateContext(tenantId);
        var storedOrder = await verify.PurchaseOrders
            .Include(value => value.OrderDetails)
            .SingleAsync(value => value.Id == created.Id);
        storedOrder.TotalAmount.Should().Be(115m);
        storedOrder.TaxAmount.Should().Be(15m);
        storedOrder.OrderDetails.Single().TaxRatePercent.Should().Be(15m);
        storedOrder.OrderDetails.Single().GrossAmount.Should().Be(115m);
    }

    [PostgreSqlFact]
    public async Task Migration_backfills_existing_purchase_order_numbers_and_lines_without_company_ownership()
    {
        fixture.EnsureEnabled();
        var schema = $"doc_identity_{Guid.NewGuid():N}";
        var tenantId = $"doc-migration-{Guid.NewGuid():N}";
        var connectionBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { SearchPath = schema };

        await using (var createSchema = new NpgsqlConnection(fixture.ConnectionString))
        {
            await createSchema.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", createSchema);
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<InventoryDbContext>()
                .UseNpgsql(connectionBuilder.ConnectionString)
                .Options;
            await using var context = new InventoryDbContext(options, new TestTenantContext(tenantId));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260917010000_AddItemExternalId");

            var supplier = new Supplier { Name = "Pre-identity supplier" };
            var item = new Item { ItemCode = "PRE-IDENTITY-ITEM", Description = "Pre-identity item", Rate = 2m };
            context.Suppliers.Add(supplier);
            context.Items.Add(item);
            await context.SaveChangesAsync();

            int purchaseOrderId;
            await using (var seedConnection = new NpgsqlConnection(connectionBuilder.ConnectionString))
            {
                await seedConnection.OpenAsync();
                await using (var insertOrder = new NpgsqlCommand(
                    """
                    INSERT INTO "PurchaseOrders" ("PONumber", "OrderDate", "SupplierId", "TotalAmount", "Status", "Notes", "TenantId", "CreatedAt")
                    VALUES (@number, @date, @supplier, 2.01, 'Pending', NULL, @tenant, now())
                    RETURNING "Id"
                    """,
                    seedConnection))
                {
                    insertOrder.Parameters.AddWithValue("number", "EXISTING-PO-731");
                    insertOrder.Parameters.AddWithValue("date", new DateTime(2024, 5, 6, 0, 0, 0, DateTimeKind.Utc));
                    insertOrder.Parameters.AddWithValue("supplier", supplier.Id);
                    insertOrder.Parameters.AddWithValue("tenant", tenantId);
                    purchaseOrderId = (int)(await insertOrder.ExecuteScalarAsync())!;
                }

                await using var insertLine = new NpgsqlCommand(
                    """
                    INSERT INTO "OrderDetails" ("PurchaseOrderId", "ItemId", "Quantity", "UnitPrice", "TenantId", "CreatedAt")
                    VALUES (@order, @item, 1, 1.005, @tenant, now())
                    """,
                    seedConnection);
                insertLine.Parameters.AddWithValue("order", purchaseOrderId);
                insertLine.Parameters.AddWithValue("item", item.Id);
                insertLine.Parameters.AddWithValue("tenant", tenantId);
                await insertLine.ExecuteNonQueryAsync();
                await insertLine.ExecuteNonQueryAsync();
            }

            await migrator.MigrateAsync();
            var mapped = await context.PurchaseOrders
                .Include(order => order.DocumentIdentity)
                .Include(order => order.OrderDetails)
                    .ThenInclude(line => line.DocumentLineIdentity)
                .AsNoTracking()
                .SingleAsync(order => order.Id == purchaseOrderId);

            mapped.PONumber.Should().Be("EXISTING-PO-731");
            mapped.DocumentId.Value.Should().NotBeEmpty();
            mapped.DocumentIdentity.Id.Should().Be(mapped.DocumentId);
            mapped.DocumentIdentity.HumanNumber.Should().Be("EXISTING-PO-731");
            mapped.DocumentIdentity.CompanyId.Should().BeNull();
            mapped.DocumentIdentity.Status.Should().Be(DocumentLifecycleStatus.Active);
            mapped.TotalAmount.Should().Be(2.02m);
            mapped.NetAmount.Should().Be(2.02m);
            mapped.DiscountAmount.Should().Be(0m);
            mapped.TaxAmount.Should().Be(0m);
            mapped.CurrencyScale.Should().Be(2);
            mapped.CalculationVersion.Should().Be(DocumentAmountCalculator.CalculationVersion);
            mapped.OrderDetails.Should().HaveCount(2);
            mapped.OrderDetails.Sum(line => line.GrossAmount).Should().Be(mapped.TotalAmount);
            mapped.OrderDetails.Should().OnlyContain(line => line.DocumentLineId.Value != Guid.Empty);
            mapped.OrderDetails.Should().OnlyContain(line => line.DocumentLineIdentity!.DocumentId == mapped.DocumentId);
            mapped.OrderDetails.Should().OnlyContain(line => line.DocumentLineIdentity!.CompanyId == null);
            mapped.OrderDetails.Should().OnlyContain(line => line.GrossAmount == 1.01m &&
                line.TaxAmount == 0m &&
                line.CalculationVersion == DocumentAmountCalculator.CalculationVersion);

            context.TaxRules.Add(new TaxRule
            {
                Code = "ROLLBACK-GUARD",
                Category = TaxCategory.Standard,
                RatePercent = 5m,
                CalculationMode = TaxCalculationMode.Exclusive,
                EffectiveFromUtc = DateTime.UtcNow,
                IsActive = true
            });
            await context.SaveChangesAsync();
            await FluentActions.Invoking(() => migrator.MigrateAsync("20260917170000_AddTaxRulesAndAmountSnapshots"))
                .Should().ThrowAsync<PostgresException>();
            (await context.TaxRules.CountAsync()).Should().Be(1);
        }
        finally
        {
            await using var dropSchema = new NpgsqlConnection(fixture.ConnectionString);
            await dropSchema.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", dropSchema);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task<PurchaseOrder> CreatePurchaseOrderAsync(
        string tenantId,
        int supplierId,
        int itemId,
        string requestKey,
        string? notes)
    {
        await using var context = fixture.CreateContext(tenantId);
        var unitOfWork = new UnitOfWork(context);
        var service = new DocumentIdentityService(context, unitOfWork, new DocumentNumberService(context, unitOfWork));
        var order = new PurchaseOrder
        {
            PONumber = "LEGACY-PO-0042",
            OrderDate = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc),
            SupplierId = supplierId,
            Notes = notes,
            Status = PurchaseOrderStatus.Pending
        };
        var details = new List<OrderDetail> { new() { ItemId = itemId, Quantity = 1, UnitPrice = 3m } };
        return await service.CreatePurchaseOrderAsync(order, details, requestKey);
    }

    private static DocumentIdentity CreateDocument(string tenantId, int? companyId, string number, int period = 2026) =>
        DocumentIdentity.Create(
            DocumentIdentityId.New(),
            tenantId,
            companyId,
            "PurchaseOrder",
            number,
            period,
            DocumentLifecycleStatus.Active,
            "DocumentIdentityPostgreSqlIntegrationTests");

    private static DocumentLineIdentity CreateLine(string tenantId, int? companyId, DocumentIdentity document) =>
        DocumentLineIdentity.Create(
            DocumentLineIdentityId.New(),
            document.Id,
            tenantId,
            companyId,
            "PurchaseOrderLine");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
