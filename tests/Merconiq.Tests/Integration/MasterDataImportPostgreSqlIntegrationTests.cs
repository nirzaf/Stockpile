using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class MasterDataImportPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
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
    public async Task Legacy_units_are_backfilled_with_provenance_and_downgrade_cannot_race_with_mapping()
    {
        fixture.EnsureEnabled();
        const string legacyPrefix = "__merconiq_legacy_unmapped_unit__:";
        var (schema, connectionString) = await CreateMigrationSchemaAsync();

        try
        {
            await using var context = CreateMigrationContext(connectionString, "legacy-a");
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260917010000_AddItemExternalId");

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

            await migrator.MigrateAsync();
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

            // Leave only the unit-identity migration applied so its downgrade can be raced directly.
            await migrator.MigrateAsync("20260916180000_AddUnitExternalId");
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
            await migrator.MigrateAsync("20260917010000_AddItemExternalId");

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
            await migrator.MigrateAsync("20260917010000_AddItemExternalId");

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

    private static MasterDataImportService CreateService(InventoryDbContext context) => new(
        new Repository<Merconiq.Core.Entities.UnitOfMeasure>(context),
        new Repository<Item>(context),
        new Repository<Company>(context),
        new Repository<Branch>(context),
        new Repository<Location>(context),
        new Repository<Supplier>(context),
        context,
        new UnitOfWork(context));
}
