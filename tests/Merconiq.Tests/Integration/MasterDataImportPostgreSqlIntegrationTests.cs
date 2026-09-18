using FluentAssertions;
using System.Text.Json;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Moq;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class MasterDataImportPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Unit_import_batch_summary_is_redacted_and_deduplicated_when_callback_replays_after_commit()
    {
        fixture.EnsureEnabled();
        var tenantId = $"unit-import-batch-{Guid.NewGuid():N}";
        await using var context = fixture.CreateContext(tenantId);
        const string csv = "external_id,code,name,decimal_places,whole_unit_only\nunit-1,EA,Each,0,false";
        var realUnitOfWork = new UnitOfWork(context);
        var replayedCallbacks = 0;
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork
            .Setup(work => work.AcquireTenantOperationLockAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string operation, CancellationToken cancellationToken) =>
                realUnitOfWork.AcquireTenantOperationLockAsync(operation, cancellationToken));
        unitOfWork
            .Setup(work => work.ExecuteInTransactionAsync(
                It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>(), It.IsAny<Func<Task<bool>>?>()))
            .Returns(async (Func<Task> operation, CancellationToken cancellationToken, Func<Task<bool>>? verifySucceeded) =>
            {
                await realUnitOfWork.ExecuteInTransactionAsync(async () =>
                {
                    replayedCallbacks++;
                    await operation();
                }, cancellationToken, verifySucceeded);
                context.ChangeTracker.Clear();
                await realUnitOfWork.ExecuteInTransactionAsync(async () =>
                {
                    replayedCallbacks++;
                    await operation();
                }, cancellationToken, verifySucceeded);
            });
        var service = CreateService(context, unitOfWork.Object);

        var dryRun = await service.ImportUnitsAsync(new ImportUnitsRequest(csv, DryRun: true));

        dryRun.Created.Should().Be(1);
        (await context.AuditLogs.CountAsync(log => log.EntityName == "MasterDataImportBatch")).Should().Be(0);
        (await context.UnitsOfMeasure.CountAsync()).Should().Be(0);

        var applied = await service.ImportUnitsAsync(new ImportUnitsRequest(csv, DryRun: false));

        (applied.Created + applied.Unchanged).Should().Be(1);
        applied.Rejected.Should().Be(0);
        replayedCallbacks.Should().Be(2,
            "the same import callback must run again after the first transaction has committed");

        await using var verification = fixture.CreateContext(tenantId);
        (await verification.UnitsOfMeasure.CountAsync()).Should().Be(1);
        var batchAudits = await verification.AuditLogs.AsNoTracking()
            .Where(log => log.EntityName == "MasterDataImportBatch")
            .ToListAsync();
        batchAudits.Should().ContainSingle();
        var audit = batchAudits[0];
        audit.TenantId.Should().Be(tenantId);
        audit.Action.Should().Be("ImportBatchCompleted");
        using var keyValues = JsonDocument.Parse(audit.KeyValues!);
        keyValues.RootElement.GetProperty("ImportType").GetString().Should().Be(nameof(UnitOfMeasure));
        keyValues.RootElement.GetProperty("BatchId").GetGuid().Should().NotBeEmpty();
        using var summary = JsonDocument.Parse(audit.NewValues!);
        summary.RootElement.GetProperty("Outcome").GetString().Should().Be("Created");
        summary.RootElement.GetProperty("RowsCreated").GetInt32().Should().Be(1);
        summary.RootElement.GetProperty("RowsRejected").GetInt32().Should().Be(0);
        summary.RootElement.GetProperty("ChangesApplied").GetBoolean().Should().BeTrue();
        audit.KeyValues.Should().NotContain("unit-1");
        audit.NewValues.Should().NotContain("unit-1").And.NotContain("Each");
    }

    [PostgreSqlFact]
    public async Task Mixed_unit_import_audits_only_rows_actually_persisted()
    {
        fixture.EnsureEnabled();
        var tenantId = $"unit-import-mixed-{Guid.NewGuid():N}";
        await using var context = fixture.CreateContext(tenantId);
        const string csv = "external_id,code,name,decimal_places,whole_unit_only\n"
            + "unit-1,EA,Each,0,false\n"
            + "unit-2,BOX,Box,99,false";

        var result = await CreateService(context).ImportUnitsAsync(new ImportUnitsRequest(csv, DryRun: false));

        result.Created.Should().Be(1);
        result.Rejected.Should().Be(1);
        (await context.UnitsOfMeasure.CountAsync()).Should().Be(0);
        var batchAudit = await context.AuditLogs.AsNoTracking()
            .SingleAsync(log => log.EntityName == "MasterDataImportBatch");
        using var summary = JsonDocument.Parse(batchAudit.NewValues!);
        summary.RootElement.GetProperty("Outcome").GetString().Should().Be("Rejected");
        summary.RootElement.GetProperty("RowsCreated").GetInt32().Should().Be(0);
        summary.RootElement.GetProperty("RowsEligibleForCreation").GetInt32().Should().Be(1);
        summary.RootElement.GetProperty("RowsRejected").GetInt32().Should().Be(1);
        summary.RootElement.GetProperty("ChangesApplied").GetBoolean().Should().BeFalse();
        batchAudit.NewValues.Should().NotContain("unit-1").And.NotContain("Each");
        batchAudit.KeyValues.Should().NotContain("unit-2");
    }

    [PostgreSqlFact]
    public async Task Location_import_rejects_cross_company_branch_and_replays_with_redacted_audits()
    {
        fixture.EnsureEnabled();
        var tenantId = $"location-import-scope-{Guid.NewGuid():N}";
        var suffix = Guid.NewGuid().ToString("N");
        var localBranchExternalId = $"local-branch-{suffix[..8]}";
        var foreignBranchExternalId = $"foreign-branch-{suffix[..8]}";
        var foreignLocationExternalId = $"location-forbidden-{suffix[..8]}";
        var localLocationExternalId = $"location-local-{suffix[..8]}";
        int localCompanyId;
        int localBranchId;

        await using (var setup = fixture.CreateContext(tenantId))
        {
            var localCompany = new Company
            {
                Code = $"LOCAL-{suffix[..8]}",
                LegalName = "Synthetic local company",
                BaseCurrency = "USD",
                CurrencyScale = 2
            };
            var foreignCompany = new Company
            {
                Code = $"FOREIGN-{suffix[..8]}",
                LegalName = "Synthetic foreign company",
                BaseCurrency = "USD",
                CurrencyScale = 2
            };
            var localBranch = new Branch
            {
                Company = localCompany,
                ExternalId = localBranchExternalId,
                Code = $"LOCAL-{suffix[..8]}",
                Name = "Synthetic local branch"
            };
            var foreignBranch = new Branch
            {
                Company = foreignCompany,
                ExternalId = foreignBranchExternalId,
                Code = $"FOREIGN-{suffix[..8]}",
                Name = "Synthetic foreign branch"
            };
            setup.AddRange(localCompany, foreignCompany, localBranch, foreignBranch);
            await setup.SaveChangesAsync();
            localCompanyId = localCompany.Id;
            localBranchId = localBranch.Id;
        }

        var foreignLocationCsv = "external_id,branch_external_id,name,address\n"
            + $"{foreignLocationExternalId},{foreignBranchExternalId},Foreign warehouse,Private address";
        ImportLocationsResult rejected;
        await using (var rejectedContext = fixture.CreateContext(tenantId))
        {
            rejected = await CreateService(rejectedContext).ImportLocationsAsync(
                new ImportLocationsRequest(foreignLocationCsv, DryRun: false, CompanyId: localCompanyId));
        }

        rejected.Created.Should().Be(0);
        rejected.Rejected.Should().Be(1);
        rejected.Rows.Single().Status.Should().Be("rejected");
        rejected.Rows.Single().Error.Should().Contain("not found in the company");

        var localLocationCsv = "external_id,branch_external_id,name,address\n"
            + $"{localLocationExternalId},{localBranchExternalId},Local warehouse,Public address";
        ImportLocationsResult created;
        await using (var createContext = fixture.CreateContext(tenantId))
        {
            created = await CreateService(createContext).ImportLocationsAsync(
                new ImportLocationsRequest(localLocationCsv, DryRun: false, CompanyId: localCompanyId));
        }

        ImportLocationsResult replayed;
        await using (var replayContext = fixture.CreateContext(tenantId))
        {
            replayed = await CreateService(replayContext).ImportLocationsAsync(
                new ImportLocationsRequest(localLocationCsv, DryRun: false, CompanyId: localCompanyId));
        }

        created.Created.Should().Be(1);
        created.Rejected.Should().Be(0);
        replayed.Created.Should().Be(0);
        replayed.Unchanged.Should().Be(1);
        replayed.Rejected.Should().Be(0);

        await using var verification = fixture.CreateContext(tenantId);
        var locations = await verification.Locations.IgnoreQueryFilters()
            .Where(location => location.TenantId == tenantId)
            .AsNoTracking()
            .ToListAsync();
        locations.Should().ContainSingle();
        locations[0].BranchId.Should().Be(localBranchId);
        locations[0].ExternalId.Should().Be(localLocationExternalId);

        var audits = await verification.AuditLogs.AsNoTracking()
            .Where(log => log.TenantId == tenantId && log.EntityName == "MasterDataImportBatch")
            .OrderBy(log => log.Id)
            .ToListAsync();
        audits.Should().HaveCount(3);
        var batchIds = new List<Guid>();
        var outcomes = new List<(string Outcome, int RowsCreated, int RowsUnchanged, int RowsRejected, bool ChangesApplied)>();
        foreach (var audit in audits)
        {
            audit.Action.Should().Be("ImportBatchCompleted");
            using var keyValues = JsonDocument.Parse(audit.KeyValues!);
            keyValues.RootElement.GetProperty("ImportType").GetString().Should().Be(nameof(Location));
            batchIds.Add(keyValues.RootElement.GetProperty("BatchId").GetGuid());

            using var summary = JsonDocument.Parse(audit.NewValues!);
            outcomes.Add((
                summary.RootElement.GetProperty("Outcome").GetString()!,
                summary.RootElement.GetProperty("RowsCreated").GetInt32(),
                summary.RootElement.GetProperty("RowsUnchanged").GetInt32(),
                summary.RootElement.GetProperty("RowsRejected").GetInt32(),
                summary.RootElement.GetProperty("ChangesApplied").GetBoolean()));
            audit.KeyValues.Should().NotContain(foreignBranchExternalId)
                .And.NotContain(localBranchExternalId)
                .And.NotContain(foreignLocationExternalId)
                .And.NotContain(localLocationExternalId);
            audit.NewValues.Should().NotContain(foreignBranchExternalId)
                .And.NotContain(localBranchExternalId)
                .And.NotContain(foreignLocationExternalId)
                .And.NotContain(localLocationExternalId)
                .And.NotContain("Foreign warehouse")
                .And.NotContain("Local warehouse")
                .And.NotContain("Private address")
                .And.NotContain("Public address");
        }

        batchIds.Should().OnlyHaveUniqueItems();
        outcomes.Should().Equal(
            ("Rejected", 0, 0, 1, false),
            ("Created", 1, 0, 0, true),
            ("Unchanged", 0, 1, 0, false));
    }

    [PostgreSqlFact]
    public async Task Controlled_onboarding_is_idempotent_and_atomic_across_owned_masters()
    {
        fixture.EnsureEnabled();
        var tenantId = $"onboarding-{Guid.NewGuid():N}";
        var serviceContext = fixture.CreateContext(tenantId);
        await using (serviceContext)
        {
            var companyCsv = "external_id,code,legal_name,trading_name,registration_number,tax_identifier,base_currency,country_code,currency_scale,is_active\n"
                + "company-1,C-1,Acme Trading,Acme,,TAX-1,QAR,QA,2,true";
            var companyResult = await CreateService(serviceContext).ImportCompaniesAsync(
                new ImportCompaniesRequest(companyCsv, false));
            companyResult.Created.Should().Be(1);
            var companyId = await serviceContext.Companies.Select(company => company.Id).SingleAsync();

            var branchCsv = "external_id,code,name,address,time_zone_id,is_active\n"
                + "branch-1,BR-1,Main,,Asia/Qatar,true";
            (await CreateService(serviceContext).ImportBranchesAsync(
                new ImportBranchesRequest(branchCsv, false, companyId))).Created.Should().Be(1);

            var locationCsv = "external_id,branch_external_id,name,address\n"
                + "location-1,branch-1,Warehouse A,\n"
                + "location-2,missing,Warehouse B,";
            var rejected = await CreateService(serviceContext).ImportLocationsAsync(
                new ImportLocationsRequest(locationCsv, false, companyId));
            rejected.Rejected.Should().Be(1);
            (await serviceContext.Locations.CountAsync()).Should().Be(0);

            var validLocationCsv = "external_id,branch_external_id,name,address\nlocation-1,branch-1,Warehouse A,";
            (await CreateService(serviceContext).ImportLocationsAsync(
                new ImportLocationsRequest(validLocationCsv, false, companyId))).Created.Should().Be(1);
            (await CreateService(serviceContext).ImportLocationsAsync(
                new ImportLocationsRequest(validLocationCsv, false, companyId))).Unchanged.Should().Be(1);

            var supplierCsv = "external_id,name,contact_person,phone,email,address\n"
                + "supplier-1,Acme Supplies,Buyer,+97400000000,buyer@example.test,Doha";
            (await CreateService(serviceContext).ImportSuppliersAsync(
                new ImportSuppliersRequest(supplierCsv, false, companyId))).Created.Should().Be(1);

            serviceContext.UnitsOfMeasure.Add(new UnitOfMeasure
            {
                ExternalId = "kg", Code = "KG", Name = "Kilogram"
            });
            await serviceContext.SaveChangesAsync();
        }

        await using (var otherTenantContext = fixture.CreateContext($"other-{Guid.NewGuid():N}"))
        {
            var foreignCompanyId = 0;
            await using (var source = fixture.CreateContext(tenantId))
                foreignCompanyId = await source.Companies.Select(company => company.Id).SingleAsync();

            await FluentActions.Invoking(() => CreateService(otherTenantContext).ImportSuppliersAsync(
                    new ImportSuppliersRequest(
                        "external_id,name,contact_person,phone,email,address\nsupplier-2,Other,,,,",
                        false, foreignCompanyId)))
                .Should().ThrowAsync<ArgumentException>();
        }

        await using var replayContext = fixture.CreateContext(tenantId);
        var itemCsv = "external_id,item_code,description,rate,base_unit_external_id,purchase_unit_external_id,sales_unit_external_id,purchase_to_base_factor,sales_to_base_factor,quantity_precision,whole_unit_only,supplier_external_id\n"
            + "item-1,SKU-1,Widget,12.50,kg,,,1,1,2,false,supplier-1";
        var company = await replayContext.Companies.SingleAsync();
        var itemResult = await CreateService(replayContext).ImportItemsAsync(
            new ImportItemsRequest(itemCsv, false, company.Id));
        itemResult.Created.Should().Be(1);
        (await replayContext.AuditLogs.CountAsync(log => log.EntityName == nameof(Item))).Should().Be(1);
    }

    [PostgreSqlFact]
    public async Task Item_import_is_idempotent_and_persists_unit_ownership()
    {
        fixture.EnsureEnabled();
        var tenantId = $"item-import-{Guid.NewGuid():N}";
        var csv = "external_id,item_code,description,rate,base_unit_external_id,purchase_unit_external_id,sales_unit_external_id,purchase_to_base_factor,sales_to_base_factor,quantity_precision,whole_unit_only\n"
            + "item-1,SKU-1,Widget,12.50,kg,,,1,1,2,false";

        await using (var setup = fixture.CreateContext(tenantId))
        {
            setup.UnitsOfMeasure.Add(new UnitOfMeasure { ExternalId = "kg", Code = "KG", Name = "Kilogram" });
            await setup.SaveChangesAsync();
        }

        await using (var firstContext = fixture.CreateContext(tenantId))
        {
            var result = await CreateService(firstContext).ImportItemsAsync(new ImportItemsRequest(csv, false));
            result.Created.Should().Be(1);
        }

        await using (var replayContext = fixture.CreateContext(tenantId))
        {
            var result = await CreateService(replayContext).ImportItemsAsync(new ImportItemsRequest(csv, false));
            result.Unchanged.Should().Be(1);
            result.Rejected.Should().Be(0);
            var item = await replayContext.Items.SingleAsync();
            item.ExternalId.Should().Be("item-1");
            item.BaseUnitId.Should().Be(await replayContext.UnitsOfMeasure.Select(unit => unit.Id).SingleAsync());
        }
    }

    [PostgreSqlFact]
    public async Task Sequential_unit_import_replay_preserves_one_row_and_audits_created_unchanged_and_conflict()
    {
        fixture.EnsureEnabled();
        var tenantId = $"unit-import-replay-{Guid.NewGuid():N}";
        const string csv = "external_id,code,name,decimal_places,whole_unit_only\nunit-1,EA,Each,0,false";
        const string conflictingCsv = "external_id,code,name,decimal_places,whole_unit_only\nunit-1,EA,Each (revised),0,false";

        ImportUnitsResult created;
        await using (var firstContext = fixture.CreateContext(tenantId))
            created = await CreateService(firstContext).ImportUnitsAsync(new ImportUnitsRequest(csv, false));

        ImportUnitsResult replayed;
        await using (var replayContext = fixture.CreateContext(tenantId))
            replayed = await CreateService(replayContext).ImportUnitsAsync(new ImportUnitsRequest(csv, false));

        ImportUnitsResult conflict;
        await using (var conflictContext = fixture.CreateContext(tenantId))
            conflict = await CreateService(conflictContext).ImportUnitsAsync(new ImportUnitsRequest(conflictingCsv, false));

        created.Created.Should().Be(1);
        created.Unchanged.Should().Be(0);
        created.Rejected.Should().Be(0);
        created.Rows.Single().Status.Should().Be("created");

        replayed.Created.Should().Be(0);
        replayed.Unchanged.Should().Be(1);
        replayed.Rejected.Should().Be(0);
        replayed.Rows.Single().Status.Should().Be("unchanged");

        conflict.Created.Should().Be(0);
        conflict.Unchanged.Should().Be(0);
        conflict.Rejected.Should().Be(1);
        conflict.Rows.Single().Status.Should().Be("rejected");
        conflict.Rows.Single().Error.Should().Contain("different unit data");

        await using var verification = fixture.CreateContext(tenantId);
        var units = await verification.UnitsOfMeasure.AsNoTracking().ToListAsync();
        units.Should().ContainSingle();
        units[0].ExternalId.Should().Be("unit-1");
        units[0].Code.Should().Be("EA");
        units[0].Name.Should().Be("Each");
        units[0].DecimalPlaces.Should().Be(0);
        units[0].IsWholeUnitOnly.Should().BeFalse();

        var audits = await verification.AuditLogs.AsNoTracking()
            .Where(log => log.TenantId == tenantId && log.EntityName == "MasterDataImportBatch")
            .OrderBy(log => log.Id)
            .ToListAsync();
        audits.Should().HaveCount(3);
        var batchIds = new List<Guid>();
        var outcomes = new List<(string Outcome, int RowsCreated, int RowsUnchanged, int RowsRejected, bool ChangesApplied)>();
        foreach (var audit in audits)
        {
            audit.Action.Should().Be("ImportBatchCompleted");
            using var keyValues = JsonDocument.Parse(audit.KeyValues!);
            keyValues.RootElement.GetProperty("ImportType").GetString().Should().Be(nameof(UnitOfMeasure));
            batchIds.Add(keyValues.RootElement.GetProperty("BatchId").GetGuid());
            using var summary = JsonDocument.Parse(audit.NewValues!);
            outcomes.Add((
                summary.RootElement.GetProperty("Outcome").GetString()!,
                summary.RootElement.GetProperty("RowsCreated").GetInt32(),
                summary.RootElement.GetProperty("RowsUnchanged").GetInt32(),
                summary.RootElement.GetProperty("RowsRejected").GetInt32(),
                summary.RootElement.GetProperty("ChangesApplied").GetBoolean()));
            audit.KeyValues.Should().NotContain("unit-1");
            audit.NewValues.Should().NotContain("unit-1").And.NotContain("Each");
        }

        batchIds.Should().OnlyHaveUniqueItems();
        outcomes.Should().Equal(
            ("Created", 1, 0, 0, true),
            ("Unchanged", 0, 1, 0, false),
            ("Rejected", 0, 0, 1, false));
    }

    [PostgreSqlFact]
    public async Task Concurrent_replays_of_the_same_unit_import_create_one_unit_and_audit_each_batch()
    {
        fixture.EnsureEnabled();
        var tenantId = $"unit-import-concurrent-{Guid.NewGuid():N}";
        const string csv = "external_id,code,name,decimal_places,whole_unit_only\nunit-1,EA,Each,0,false";
        var firstApplicationName = $"master-import-first-{Guid.NewGuid():N}";
        var secondApplicationName = $"master-import-second-{Guid.NewGuid():N}";
        var firstConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = firstApplicationName
        }.ConnectionString;
        var secondConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = secondApplicationName
        }.ConnectionString;

        await using var firstContext = CreateMigrationContext(firstConnectionString, tenantId);
        await using var secondContext = CreateMigrationContext(secondConnectionString, tenantId);
        var firstLockAcquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var realFirstUnitOfWork = new UnitOfWork(firstContext);
        var firstUnitOfWork = new Mock<IUnitOfWork>();
        firstUnitOfWork
            .Setup(work => work.ExecuteInTransactionAsync(
                It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>(), It.IsAny<Func<Task<bool>>?>()))
            .Returns((Func<Task> operation, CancellationToken cancellationToken, Func<Task<bool>>? verifySucceeded) =>
                realFirstUnitOfWork.ExecuteInTransactionAsync(operation, cancellationToken, verifySucceeded));
        firstUnitOfWork
            .Setup(work => work.AcquireTenantOperationLockAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string operation, CancellationToken cancellationToken) =>
            {
                await realFirstUnitOfWork.AcquireTenantOperationLockAsync(operation, cancellationToken);
                firstLockAcquired.TrySetResult(true);
                await releaseFirstLock.Task.WaitAsync(cancellationToken);
            });

        var firstImport = CreateService(firstContext, firstUnitOfWork.Object)
            .ImportUnitsAsync(new ImportUnitsRequest(csv, false));
        try
        {
            await firstLockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            releaseFirstLock.TrySetResult(true);
            await firstImport;
            throw;
        }

        var secondImport = CreateService(secondContext).ImportUnitsAsync(new ImportUnitsRequest(csv, false));
        try
        {
            await WaitForAdvisoryLockWaitAsync(fixture.ConnectionString, secondApplicationName);
        }
        catch
        {
            releaseFirstLock.TrySetResult(true);
            await Task.WhenAll(firstImport, secondImport);
            throw;
        }
        releaseFirstLock.TrySetResult(true);

        var results = await Task.WhenAll(firstImport, secondImport);

        results.Sum(result => result.Created).Should().Be(1);
        results.Sum(result => result.Unchanged).Should().Be(1);
        results.Sum(result => result.Rejected).Should().Be(0);

        await using var verification = fixture.CreateContext(tenantId);
        (await verification.UnitsOfMeasure.CountAsync()).Should().Be(1);
        var batchAudits = await verification.AuditLogs.AsNoTracking()
            .Where(log => log.TenantId == tenantId && log.EntityName == "MasterDataImportBatch")
            .ToListAsync();
        batchAudits.Should().HaveCount(2);
        var outcomes = new List<string?>();
        foreach (var audit in batchAudits)
        {
            using var summary = JsonDocument.Parse(audit.NewValues!);
            outcomes.Add(summary.RootElement.GetProperty("Outcome").GetString());
        }
        outcomes.Should().BeEquivalentTo(new[] { "Created", "Unchanged" });
    }

    [PostgreSqlFact]
    public async Task Concurrent_replays_of_the_same_company_import_create_one_company_and_audit_each_batch()
    {
        fixture.EnsureEnabled();
        var tenantId = $"company-import-concurrent-{Guid.NewGuid():N}";
        var suffix = Guid.NewGuid().ToString("N");
        var externalId = $"company-{suffix}";
        var csv = "external_id,code,legal_name,trading_name,registration_number,tax_identifier,base_currency,country_code,currency_scale,is_active\n"
            + $"{externalId},C-{suffix},Synthetic Company,Synthetic Trading,REG-{suffix},TAX-{suffix},QAR,QA,2,true";
        var firstApplicationName = $"company-import-first-{Guid.NewGuid():N}";
        var secondApplicationName = $"company-import-second-{Guid.NewGuid():N}";
        var firstConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = firstApplicationName
        }.ConnectionString;
        var secondConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = secondApplicationName
        }.ConnectionString;

        await using var firstContext = CreateMigrationContext(firstConnectionString, tenantId);
        await using var secondContext = CreateMigrationContext(secondConnectionString, tenantId);
        var firstLockAcquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var realFirstUnitOfWork = new UnitOfWork(firstContext);
        var firstUnitOfWork = new Mock<IUnitOfWork>();
        firstUnitOfWork
            .Setup(work => work.ExecuteInTransactionAsync(
                It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>(), It.IsAny<Func<Task<bool>>?>()))
            .Returns((Func<Task> operation, CancellationToken cancellationToken, Func<Task<bool>>? verifySucceeded) =>
                realFirstUnitOfWork.ExecuteInTransactionAsync(operation, cancellationToken, verifySucceeded));
        firstUnitOfWork
            .Setup(work => work.AcquireTenantOperationLockAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string operation, CancellationToken cancellationToken) =>
            {
                await realFirstUnitOfWork.AcquireTenantOperationLockAsync(operation, cancellationToken);
                firstLockAcquired.TrySetResult(true);
                await releaseFirstLock.Task.WaitAsync(cancellationToken);
            });

        var firstImport = CreateService(firstContext, firstUnitOfWork.Object)
            .ImportCompaniesAsync(new ImportCompaniesRequest(csv, false));
        try
        {
            await firstLockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            releaseFirstLock.TrySetResult(true);
            await firstImport;
            throw;
        }

        var secondImport = CreateService(secondContext).ImportCompaniesAsync(new ImportCompaniesRequest(csv, false));
        try
        {
            await WaitForAdvisoryLockWaitAsync(fixture.ConnectionString, secondApplicationName);
        }
        catch
        {
            releaseFirstLock.TrySetResult(true);
            await Task.WhenAll(firstImport, secondImport);
            throw;
        }
        releaseFirstLock.TrySetResult(true);

        var results = await Task.WhenAll(firstImport, secondImport);

        results.Sum(result => result.Created).Should().Be(1);
        results.Sum(result => result.Unchanged).Should().Be(1);
        results.Sum(result => result.Rejected).Should().Be(0);
        results.SelectMany(result => result.Rows).Select(row => row.Status)
            .Should().BeEquivalentTo(new[] { "created", "unchanged" });

        await using var verification = fixture.CreateContext(tenantId);
        var companies = await verification.Companies.AsNoTracking().ToListAsync();
        companies.Should().ContainSingle();
        companies[0].ExternalId.Should().Be(externalId);
        companies[0].LegalName.Should().Be("Synthetic Company");

        var batchAudits = await verification.AuditLogs.AsNoTracking()
            .Where(log => log.TenantId == tenantId && log.EntityName == "MasterDataImportBatch")
            .ToListAsync();
        batchAudits.Should().HaveCount(2);
        var batchIds = new List<Guid>();
        var outcomes = new List<(string Outcome, int RowsCreated, int RowsUnchanged, int RowsRejected, bool ChangesApplied)>();
        foreach (var audit in batchAudits)
        {
            audit.Action.Should().Be("ImportBatchCompleted");
            using var keyValues = JsonDocument.Parse(audit.KeyValues!);
            keyValues.RootElement.GetProperty("ImportType").GetString().Should().Be(nameof(Company));
            batchIds.Add(keyValues.RootElement.GetProperty("BatchId").GetGuid());
            using var summary = JsonDocument.Parse(audit.NewValues!);
            outcomes.Add((
                summary.RootElement.GetProperty("Outcome").GetString()!,
                summary.RootElement.GetProperty("RowsCreated").GetInt32(),
                summary.RootElement.GetProperty("RowsUnchanged").GetInt32(),
                summary.RootElement.GetProperty("RowsRejected").GetInt32(),
                summary.RootElement.GetProperty("ChangesApplied").GetBoolean()));
            audit.KeyValues.Should().NotContain(externalId);
            audit.NewValues.Should().NotContain(externalId)
                .And.NotContain("Synthetic Company")
                .And.NotContain($"TAX-{suffix}")
                .And.NotContain($"REG-{suffix}");
        }

        batchIds.Should().OnlyHaveUniqueItems();
        outcomes.Should().BeEquivalentTo(new[]
        {
            ("Created", 1, 0, 0, true),
            ("Unchanged", 0, 1, 0, false)
        });
    }

    [PostgreSqlFact]
    public async Task Concurrent_replays_of_the_same_supplier_import_create_one_supplier_and_audit_each_batch()
    {
        fixture.EnsureEnabled();
        var tenantId = $"supplier-import-concurrent-{Guid.NewGuid():N}";
        var suffix = Guid.NewGuid().ToString("N");
        var externalId = $"supplier-{suffix}";
        var csv = "external_id,name,contact_person,phone,email,address\n"
            + $"{externalId},Synthetic Supplier,Synthetic Contact,+10000000000,"
            + $"supplier-{suffix}@example.test,Synthetic Address";
        var firstApplicationName = $"supplier-import-first-{Guid.NewGuid():N}";
        var secondApplicationName = $"supplier-import-second-{Guid.NewGuid():N}";
        var firstConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = firstApplicationName
        }.ConnectionString;
        var secondConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            ApplicationName = secondApplicationName
        }.ConnectionString;

        await using var firstContext = CreateMigrationContext(firstConnectionString, tenantId);
        await using var secondContext = CreateMigrationContext(secondConnectionString, tenantId);
        var firstLockAcquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstLock = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var realFirstUnitOfWork = new UnitOfWork(firstContext);
        var firstUnitOfWork = new Mock<IUnitOfWork>();
        firstUnitOfWork
            .Setup(work => work.ExecuteInTransactionAsync(
                It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>(), It.IsAny<Func<Task<bool>>?>()))
            .Returns((Func<Task> operation, CancellationToken cancellationToken, Func<Task<bool>>? verifySucceeded) =>
                realFirstUnitOfWork.ExecuteInTransactionAsync(operation, cancellationToken, verifySucceeded));
        firstUnitOfWork
            .Setup(work => work.AcquireTenantOperationLockAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string operation, CancellationToken cancellationToken) =>
            {
                await realFirstUnitOfWork.AcquireTenantOperationLockAsync(operation, cancellationToken);
                firstLockAcquired.TrySetResult(true);
                await releaseFirstLock.Task.WaitAsync(cancellationToken);
            });

        var firstImport = CreateService(firstContext, firstUnitOfWork.Object)
            .ImportSuppliersAsync(new ImportSuppliersRequest(csv, false));
        try
        {
            await firstLockAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            releaseFirstLock.TrySetResult(true);
            await firstImport;
            throw;
        }

        var secondImport = CreateService(secondContext).ImportSuppliersAsync(new ImportSuppliersRequest(csv, false));
        try
        {
            await WaitForAdvisoryLockWaitAsync(fixture.ConnectionString, secondApplicationName);
        }
        catch
        {
            releaseFirstLock.TrySetResult(true);
            await Task.WhenAll(firstImport, secondImport);
            throw;
        }
        releaseFirstLock.TrySetResult(true);

        var results = await Task.WhenAll(firstImport, secondImport);

        results.Sum(result => result.Created).Should().Be(1);
        results.Sum(result => result.Unchanged).Should().Be(1);
        results.Sum(result => result.Rejected).Should().Be(0);
        results.SelectMany(result => result.Rows).Select(row => row.Status)
            .Should().BeEquivalentTo(new[] { "created", "unchanged" });

        await using var verification = fixture.CreateContext(tenantId);
        var suppliers = await verification.Suppliers.AsNoTracking().ToListAsync();
        suppliers.Should().ContainSingle();
        suppliers[0].ExternalId.Should().Be(externalId);
        suppliers[0].Name.Should().Be("Synthetic Supplier");

        var batchAudits = await verification.AuditLogs.AsNoTracking()
            .Where(log => log.TenantId == tenantId && log.EntityName == "MasterDataImportBatch")
            .ToListAsync();
        batchAudits.Should().HaveCount(2);
        var batchIds = new List<Guid>();
        var outcomes = new List<(string Outcome, int RowsCreated, int RowsUnchanged, int RowsRejected, bool ChangesApplied)>();
        foreach (var audit in batchAudits)
        {
            audit.Action.Should().Be("ImportBatchCompleted");
            using var keyValues = JsonDocument.Parse(audit.KeyValues!);
            keyValues.RootElement.GetProperty("ImportType").GetString().Should().Be(nameof(Supplier));
            batchIds.Add(keyValues.RootElement.GetProperty("BatchId").GetGuid());
            using var summary = JsonDocument.Parse(audit.NewValues!);
            outcomes.Add((
                summary.RootElement.GetProperty("Outcome").GetString()!,
                summary.RootElement.GetProperty("RowsCreated").GetInt32(),
                summary.RootElement.GetProperty("RowsUnchanged").GetInt32(),
                summary.RootElement.GetProperty("RowsRejected").GetInt32(),
                summary.RootElement.GetProperty("ChangesApplied").GetBoolean()));
            audit.KeyValues.Should().NotContain(externalId);
            audit.NewValues.Should().NotContain(externalId).And.NotContain("Synthetic Supplier");
        }

        batchIds.Should().OnlyHaveUniqueItems();
        outcomes.Should().BeEquivalentTo(new[]
        {
            ("Created", 1, 0, 0, true),
            ("Unchanged", 0, 1, 0, false)
        });
    }

    [PostgreSqlFact]
    public async Task Soft_deleted_master_keys_are_rejected_by_external_id_and_natural_code()
    {
        fixture.EnsureEnabled();
        var tenantId = $"master-import-deleted-{Guid.NewGuid():N}";
        await using var context = fixture.CreateContext(tenantId);

        context.UnitsOfMeasure.Add(new UnitOfMeasure
        {
            ExternalId = "deleted-unit-id",
            Code = "DELETED-UNIT-CODE",
            Name = "Deleted unit",
            IsDeleted = true
        });
        await context.SaveChangesAsync();

        const string unitCsv = "external_id,code,name,decimal_places,whole_unit_only\n"
            + "deleted-unit-id,NEW-UNIT-CODE,Replacement,0,false\n"
            + "new-unit-id,DELETED-UNIT-CODE,Replacement,0,false";
        var unitResult = await CreateService(context).ImportUnitsAsync(new ImportUnitsRequest(unitCsv, false));

        unitResult.Created.Should().Be(0);
        unitResult.Rejected.Should().Be(2);
        unitResult.Rows.Should().OnlyContain(row => row.Status == "rejected" && row.Error!.Contains("deleted unit"));
        (await context.UnitsOfMeasure.IgnoreQueryFilters()
            .CountAsync(unit => unit.TenantId == tenantId)).Should().Be(1);

        context.UnitsOfMeasure.Add(new UnitOfMeasure { ExternalId = "kg", Code = "KG", Name = "Kilogram" });
        context.Items.Add(new Item
        {
            ExternalId = "deleted-item-id",
            ItemCode = "DELETED-ITEM-CODE",
            Description = "Deleted item",
            IsDeleted = true
        });
        await context.SaveChangesAsync();

        const string itemCsv = "external_id,item_code,description,rate,base_unit_external_id,purchase_unit_external_id,sales_unit_external_id,purchase_to_base_factor,sales_to_base_factor,quantity_precision,whole_unit_only\n"
            + "deleted-item-id,NEW-ITEM-CODE,Replacement,1,kg,,,1,1,2,false\n"
            + "new-item-id,DELETED-ITEM-CODE,Replacement,1,kg,,,1,1,2,false";
        var itemResult = await CreateService(context).ImportItemsAsync(new ImportItemsRequest(itemCsv, false));

        itemResult.Created.Should().Be(0);
        itemResult.Rejected.Should().Be(2);
        itemResult.Rows.Should().OnlyContain(row => row.Status == "rejected" && row.Error!.Contains("deleted item"));
        (await context.Items.IgnoreQueryFilters()
            .CountAsync(item => item.TenantId == tenantId)).Should().Be(1);
    }

    [PostgreSqlFact]
    public async Task Supplier_import_rejects_a_soft_deleted_external_id_without_partial_mutation()
    {
        fixture.EnsureEnabled();
        var tenantId = $"supplier-import-deleted-{Guid.NewGuid():N}";
        var suffix = Guid.NewGuid().ToString("N");
        const string deletedExternalId = "deleted-supplier";
        var newExternalId = $"new-supplier-{suffix}";

        await using (var seed = fixture.CreateContext(tenantId))
        {
            seed.Suppliers.Add(new Supplier
            {
                ExternalId = deletedExternalId,
                Name = "Original deleted supplier",
                ContactPerson = "Original contact",
                Phone = "+10000000001",
                Email = "original@example.test",
                Address = "Original address",
                IsDeleted = true
            });
            await seed.SaveChangesAsync();
        }

        var csv = "external_id,name,contact_person,phone,email,address\n"
            + $"{newExternalId},New Supplier,New Contact,+10000000002,new@example.test,New Address\n"
            + "deleted-supplier,Replacement Supplier,Replacement Contact,+10000000003,"
            + "replacement@example.test,Replacement Address";
        await using var importContext = fixture.CreateContext(tenantId);

        var result = await CreateService(importContext)
            .ImportSuppliersAsync(new ImportSuppliersRequest(csv, false));

        result.Rejected.Should().Be(1);
        result.Rows.Should().ContainSingle(row => row.ExternalId == deletedExternalId
            && row.Status == "rejected" && row.Error!.Contains("deleted supplier"));

        await using var verification = fixture.CreateContext(tenantId);
        var suppliers = await verification.Suppliers.IgnoreQueryFilters().AsNoTracking()
            .Where(supplier => supplier.TenantId == tenantId)
            .ToListAsync();
        suppliers.Should().ContainSingle();
        suppliers[0].ExternalId.Should().Be(deletedExternalId);
        suppliers[0].Name.Should().Be("Original deleted supplier");
        suppliers[0].ContactPerson.Should().Be("Original contact");
        suppliers[0].Phone.Should().Be("+10000000001");
        suppliers[0].Email.Should().Be("original@example.test");
        suppliers[0].Address.Should().Be("Original address");
        suppliers[0].IsDeleted.Should().BeTrue();

        var batchAudit = await verification.AuditLogs.AsNoTracking()
            .SingleAsync(log => log.TenantId == tenantId && log.EntityName == "MasterDataImportBatch");
        using var summary = JsonDocument.Parse(batchAudit.NewValues!);
        summary.RootElement.GetProperty("Outcome").GetString().Should().Be("Rejected");
        summary.RootElement.GetProperty("RowsCreated").GetInt32().Should().Be(0);
        summary.RootElement.GetProperty("RowsEligibleForCreation").GetInt32().Should().Be(1);
        summary.RootElement.GetProperty("RowsRejected").GetInt32().Should().Be(1);
        summary.RootElement.GetProperty("ChangesApplied").GetBoolean().Should().BeFalse();
    }

    [PostgreSqlFact]
    public async Task Legacy_units_are_backfilled_with_provenance_and_downgrade_cannot_race_with_mapping()
    {
        fixture.EnsureEnabled();
        const string legacyPrefix = "__merconiq_legacy_unmapped_unit__:";
        var (schema, connectionString) = await CreateMigrationSchemaAsync();

        try
        {
            await using var context = CreateMigrationContext(connectionString, "legacy-a");
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260916170000_AddItemQuantityConventions");

            await using (var seed = new NpgsqlConnection(connectionString))
            {
                await seed.OpenAsync();
                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO "UnitsOfMeasure"
                        ("TenantId", "Code", "Name", "DecimalPlaces", "IsWholeUnitOnly", "IsDeleted", "CreatedAt")
                    VALUES
                        ('legacy-a', 'EA', 'Each', 0, false, false, now()),
                        ('legacy-a', 'BOX', 'Box', 0, false, false, now()),
                        ('legacy-b', 'KG', 'Kilogram', 3, false, false, now())
                    """, seed);
                await command.ExecuteNonQueryAsync();
            }

            // Apply only the migration under test; unrelated later migrations are not needed here.
            await migrator.MigrateAsync("20260916180000_AddUnitExternalId");
            context.ChangeTracker.Clear();
            var units = await context.UnitsOfMeasure.IgnoreQueryFilters().AsNoTracking()
                .OrderBy(unit => unit.Id).ToListAsync();

            units.Should().HaveCount(3);
            units.Should().OnlyContain(unit => unit.ExternalId == $"{legacyPrefix}{unit.Id}");
            units.Where(unit => unit.TenantId == "legacy-a")
                .Select(unit => unit.ExternalId).Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(2);

            await using (var verify = new NpgsqlConnection(connectionString))
            {
                await verify.OpenAsync();
                await using var command = new NpgsqlCommand(
                    "SELECT count(*) FROM \"UnitExternalIdBackfillProvenance\" WHERE \"MigrationId\" = @migrationId AND \"PreviousExternalId\" = ''",
                    verify);
                command.Parameters.AddWithValue("migrationId", "20260916180000_AddUnitExternalId");
                ((long)(await command.ExecuteScalarAsync())!).Should().Be(3);
            }

            var unitToMap = units.Single(unit => unit.TenantId == "legacy-a" && unit.Code == "EA");
            var mappedExternalId = "owner-approved-source-unit-42";
            var applicationName = $"unit-id-downgrade-{Guid.NewGuid():N}";

            await using var writer = new NpgsqlConnection(connectionString);
            await writer.OpenAsync();
            await using var writerTransaction = await writer.BeginTransactionAsync();
            await using (var update = new NpgsqlCommand(
                "UPDATE \"UnitsOfMeasure\" SET \"ExternalId\" = @externalId WHERE \"Id\" = @id",
                writer,
                writerTransaction))
            {
                update.Parameters.AddWithValue("externalId", mappedExternalId);
                update.Parameters.AddWithValue("id", unitToMap.Id);
                (await update.ExecuteNonQueryAsync()).Should().Be(1);
            }

            var migrationConnection = new NpgsqlConnectionStringBuilder(connectionString)
            {
                ApplicationName = applicationName
            }.ConnectionString;
            await using var downgradeContext = CreateMigrationContext(migrationConnection, "legacy-a");
            var downgradeTask = downgradeContext.GetService<IMigrator>()
                .MigrateAsync("20260916170000_AddItemQuantityConventions");

            Exception? lockWaitFailure = null;
            try
            {
                await WaitForMigrationLockAsync(connectionString, applicationName);
            }
            catch (Exception exception)
            {
                lockWaitFailure = exception;
            }

            await writerTransaction.CommitAsync();
            var downgradeFailure = await FluentActions.Invoking(() => downgradeTask)
                .Should().ThrowAsync<PostgresException>();
            lockWaitFailure.Should().BeNull("the downgrade must wait for the concurrent unit mapping transaction");
            downgradeFailure.Which.Message.Should().Contain("Cannot safely downgrade unit external IDs");

            await using var verifyMapping = new NpgsqlConnection(connectionString);
            await verifyMapping.OpenAsync();
            await using var verifyCommand = new NpgsqlCommand(
                "SELECT \"ExternalId\" FROM \"UnitsOfMeasure\" WHERE \"Id\" = @id",
                verifyMapping);
            verifyCommand.Parameters.AddWithValue("id", unitToMap.Id);
            (await verifyCommand.ExecuteScalarAsync()).Should().Be(mappedExternalId);
        }
        finally
        {
            await DropMigrationSchemaAsync(schema);
        }
    }

    [PostgreSqlFact]
    public async Task Forward_migration_backfills_only_blank_ids_and_restores_them_on_downgrade()
    {
        fixture.EnsureEnabled();
        const string legacyPrefix = "__merconiq_legacy_unmapped_unit__:";
        var (schema, connectionString) = await CreateMigrationSchemaAsync();

        try
        {
            await using var context = CreateMigrationContext(connectionString, "preserved");
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260916170000_AddItemQuantityConventions");

            await using (var seed = new NpgsqlConnection(connectionString))
            {
                await seed.OpenAsync();
                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO "UnitsOfMeasure"
                        ("TenantId", "Code", "Name", "DecimalPlaces", "IsWholeUnitOnly", "IsDeleted", "CreatedAt")
                    VALUES
                        ('preserved', 'EA', 'Each', 0, false, false, now()),
                        ('unmapped', 'BOX', 'Box', 0, false, false, now())
                    """, seed);
                await command.ExecuteNonQueryAsync();

                await using var legacyMigration = new NpgsqlCommand(
                    """
                    ALTER TABLE "UnitsOfMeasure"
                        ADD COLUMN "ExternalId" character varying(128) NOT NULL DEFAULT '';
                    CREATE UNIQUE INDEX "IX_UnitsOfMeasure_TenantId_ExternalId"
                        ON "UnitsOfMeasure" ("TenantId", "ExternalId");
                    UPDATE "UnitsOfMeasure" SET "ExternalId" = 'owner-mapped-unit-1' WHERE "Code" = 'EA';
                    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260916180000_AddUnitExternalId', '10.0.9');
                    """, seed);
                await legacyMigration.ExecuteNonQueryAsync();
            }

            await migrator.MigrateAsync();
            context.ChangeTracker.Clear();
            var upgraded = await context.UnitsOfMeasure.IgnoreQueryFilters().AsNoTracking()
                .ToDictionaryAsync(unit => unit.Code);
            upgraded["EA"].ExternalId.Should().Be("owner-mapped-unit-1");
            upgraded["BOX"].ExternalId.Should().Be($"{legacyPrefix}{upgraded["BOX"].Id}");

            await migrator.MigrateAsync("20260918000000_AddMasterDataExternalIds");
            context.ChangeTracker.Clear();
            var downgraded = await context.UnitsOfMeasure.IgnoreQueryFilters().AsNoTracking()
                .ToDictionaryAsync(unit => unit.Code);
            downgraded["EA"].ExternalId.Should().Be("owner-mapped-unit-1");
            downgraded["BOX"].ExternalId.Should().BeEmpty();

            await migrator.MigrateAsync();
            context.ChangeTracker.Clear();
            var replayed = await context.UnitsOfMeasure.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(unit => unit.Code == "BOX");
            replayed.ExternalId.Should().Be($"{legacyPrefix}{replayed.Id}");
        }
        finally
        {
            await DropMigrationSchemaAsync(schema);
        }
    }

    [PostgreSqlFact]
    public async Task Forward_migration_rejects_case_insensitive_placeholder_collisions_atomically()
    {
        fixture.EnsureEnabled();
        const string legacyPrefix = "__merconiq_legacy_unmapped_unit__:";
        var (schema, connectionString) = await CreateMigrationSchemaAsync();

        try
        {
            await using var context = CreateMigrationContext(connectionString, "collision");
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260916170000_AddItemQuantityConventions");

            await using (var seed = new NpgsqlConnection(connectionString))
            {
                await seed.OpenAsync();
                await using var command = new NpgsqlCommand(
                    """
                    INSERT INTO "UnitsOfMeasure"
                        ("TenantId", "Code", "Name", "DecimalPlaces", "IsWholeUnitOnly", "IsDeleted", "CreatedAt")
                    VALUES ('collision', 'EA', 'Each', 0, false, false, now());
                    ALTER TABLE "UnitsOfMeasure"
                        ADD COLUMN "ExternalId" character varying(128) NOT NULL DEFAULT '';
                    CREATE UNIQUE INDEX "IX_UnitsOfMeasure_TenantId_ExternalId"
                        ON "UnitsOfMeasure" ("TenantId", "ExternalId");
                    """, seed);
                await command.ExecuteNonQueryAsync();

                int legacyUnitId;
                await using (var getId = new NpgsqlCommand(
                    "SELECT \"Id\" FROM \"UnitsOfMeasure\" WHERE \"Code\" = 'EA'", seed))
                {
                    legacyUnitId = (int)(await getId.ExecuteScalarAsync())!;
                }

                await using var addCollision = new NpgsqlCommand(
                    """
                    INSERT INTO "UnitsOfMeasure"
                        ("TenantId", "Code", "Name", "DecimalPlaces", "IsWholeUnitOnly", "IsDeleted", "CreatedAt", "ExternalId")
                    VALUES ('collision', 'EA-ALT', 'Alternate each', 0, false, false, now(), @externalId);
                    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260916180000_AddUnitExternalId', '10.0.9');
                    """, seed);
                addCollision.Parameters.AddWithValue("externalId", $"{legacyPrefix.ToUpperInvariant()}{legacyUnitId}");
                await addCollision.ExecuteNonQueryAsync();
            }

            var failure = await FluentActions.Invoking(() => migrator.MigrateAsync())
                .Should().ThrowAsync<PostgresException>();
            failure.Which.Message.Should().Contain("existing external ID would collide");

            await using var verify = new NpgsqlConnection(connectionString);
            await verify.OpenAsync();
            await using var read = new NpgsqlCommand(
                "SELECT \"ExternalId\" FROM \"UnitsOfMeasure\" WHERE \"Code\" = 'EA'", verify);
            ((string)(await read.ExecuteScalarAsync())!).Should().BeEmpty();
            await using var provenance = new NpgsqlCommand(
                "SELECT to_regclass(@tableName) IS NOT NULL", verify);
            provenance.Parameters.AddWithValue("tableName", "\"UnitExternalIdBackfillProvenance\"");
            (await provenance.ExecuteScalarAsync()).Should().Be(false);
        }
        finally
        {
            await DropMigrationSchemaAsync(schema);
        }
    }

    private async Task<(string Schema, string ConnectionString)> CreateMigrationSchemaAsync()
    {
        var schema = $"master_data_migration_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            SearchPath = schema
        }.ConnectionString;
        return (schema, connectionString);
    }

    private async Task DropMigrationSchemaAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static InventoryDbContext CreateMigrationContext(string connectionString, string tenantId)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new InventoryDbContext(options, new TestTenantContext(tenantId));
    }

    private static async Task WaitForAdvisoryLockWaitAsync(string connectionString, string applicationName)
    {
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name = @applicationName " +
            "AND wait_event_type = 'Lock' AND query LIKE '%pg_advisory_xact_lock%')",
            observer);
        command.Parameters.AddWithValue("applicationName", applicationName);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((bool)(await command.ExecuteScalarAsync())!)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException("The second import did not wait for the first import's tenant lock.");
    }

    private static async Task WaitForMigrationLockAsync(string connectionString, string applicationName)
    {
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name = @applicationName AND wait_event_type = 'Lock' AND state = 'active')",
            observer);
        command.Parameters.AddWithValue("applicationName", applicationName);

        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((bool)(await command.ExecuteScalarAsync())!)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException("The downgrade did not wait for the concurrent unit update lock.");
    }

    private static MasterDataImportService CreateService(InventoryDbContext context, IUnitOfWork? unitOfWork = null) => new(
        new Repository<Merconiq.Core.Entities.UnitOfMeasure>(context),
        new Repository<Item>(context),
        new Repository<Company>(context),
        new Repository<Branch>(context),
        new Repository<Location>(context),
        new Repository<Supplier>(context),
        context,
        unitOfWork ?? new UnitOfWork(context));
}
