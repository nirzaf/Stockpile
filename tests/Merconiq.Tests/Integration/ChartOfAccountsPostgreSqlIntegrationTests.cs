using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class ChartOfAccountsPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Migration_downgrade_serializes_concurrent_account_insert_and_preserves_catalog_and_migration_state()
    {
        fixture.EnsureEnabled();
        const string predecessorMigration = "20260918200446_AddCompanyScopedCustomers";
        const string chartMigration = "20260918220745_AddCompanyChartOfAccounts";
        var schemaName = $"chart_downgrade_{Guid.NewGuid():N}";
        var tenantId = $"downgrade-{Guid.NewGuid():N}"[..40];
        var applicationName = $"chart-down-{Guid.NewGuid():N}";

        await using (var createSchema = new NpgsqlConnection(fixture.ConnectionString))
        {
            await createSchema.OpenAsync();
            await using var command = createSchema.CreateCommand();
            command.CommandText = $"CREATE SCHEMA \"{schemaName}\"";
            await command.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            SearchPath = schemaName
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        var migrationConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName
        }.ConnectionString;
        var migrationOptions = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(migrationConnectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        Task? downgradeTask = null;
        try
        {
            int companyId;
            await using (var setup = new InventoryDbContext(options, new TestTenantContext(tenantId)))
            {
                await setup.Database.MigrateAsync();
                var company = new Company
                {
                    Code = "SYN-CHART-DOWNGRADE",
                    LegalName = "Synthetic downgrade test company",
                    BaseCurrency = "TST"
                };
                setup.Companies.Add(company);
                await setup.SaveChangesAsync();
                companyId = company.Id;
            }

            await using var migrationContext = new InventoryDbContext(
                migrationOptions,
                new TestTenantContext(tenantId));
            int accountId;
            await using (var writer = new NpgsqlConnection(connectionString))
            {
                await writer.OpenAsync();
                await using var writerTransaction = await writer.BeginTransactionAsync();
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO "ChartOfAccounts"
                        ("CompanyId", "AccountCode", "Name", "AccountType", "IsGroupAccount", "IsActive", "TenantId", "CreatedAt")
                    VALUES
                        (@companyId, 'SYN-CONCURRENT-ACCOUNT', 'Synthetic concurrent account', 'Synthetic test label', FALSE, TRUE, @tenantId, @createdAt)
                    RETURNING "Id"
                    """,
                    writer,
                    writerTransaction);
                insert.Parameters.AddWithValue("companyId", companyId);
                insert.Parameters.AddWithValue("tenantId", tenantId);
                insert.Parameters.AddWithValue("createdAt", DateTime.UtcNow);
                accountId = (int)(await insert.ExecuteScalarAsync())!;

                var migrationTask = migrationContext.GetService<IMigrator>().MigrateAsync(predecessorMigration);
                downgradeTask = migrationTask;

                await using (var observer = new NpgsqlConnection(connectionString))
                {
                    await observer.OpenAsync();
                    var waitingForLock = await WaitForChartDowngradeLockAsync(
                        observer,
                        schemaName,
                        applicationName,
                        TimeSpan.FromSeconds(15));
                    if (!waitingForLock)
                    {
                        await writerTransaction.RollbackAsync();
                        try { await migrationTask.WaitAsync(TimeSpan.FromSeconds(15)); }
                        catch { /* The expected test failure below reports the missing lock wait. */ }
                    }

                    waitingForLock.Should().BeTrue(
                        "the rollback must acquire its table lock before checking rows and wait for the in-flight insert");
                }

                await writerTransaction.CommitAsync();

                var failure = await FluentActions.Awaiting(() => migrationTask)
                    .Should().ThrowAsync<PostgresException>();
                failure.Which.SqlState.Should().Be("P0001");
                failure.Which.MessageText.Should().Contain(
                    "Cannot downgrade company chart of accounts while account rows exist.");

                (await migrationContext.ChartOfAccounts.SingleAsync(account => account.Id == accountId))
                    .AccountCode.Should().Be("SYN-CONCURRENT-ACCOUNT");
                var appliedMigrations = await migrationContext.Database.GetAppliedMigrationsAsync();
                appliedMigrations.Should().Contain(chartMigration);
                appliedMigrations.Last().Should().Be(chartMigration);
            }
        }
        finally
        {
            if (downgradeTask is not null)
            {
                try { await downgradeTask.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch { /* Preserve the originating assertion or migration failure. */ }
            }

            await using var dropSchema = new NpgsqlConnection(fixture.ConnectionString);
            await dropSchema.OpenAsync();
            await using var dropCommand = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE",
                dropSchema);
            await dropCommand.ExecuteNonQueryAsync();
        }
    }

    [PostgreSqlFact]
    public async Task Migration_preserves_companies_and_adds_no_default_chart()
    {
        fixture.EnsureEnabled();
        const string predecessorMigration = "20260918200446_AddCompanyScopedCustomers";
        var schemaName = $"chart_migration_{Guid.NewGuid():N}";
        var tenantId = $"migration-{Guid.NewGuid():N}"[..40];

        await using (var createSchema = new NpgsqlConnection(fixture.ConnectionString))
        {
            await createSchema.OpenAsync();
            await using var command = createSchema.CreateCommand();
            command.CommandText = $"CREATE SCHEMA \"{schemaName}\"";
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                SearchPath = schemaName
            }.ConnectionString;
            var options = new DbContextOptionsBuilder<InventoryDbContext>()
                .UseNpgsql(connectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;

            await using var db = new InventoryDbContext(options, new TestTenantContext(tenantId));
            await db.Database.MigrateAsync(predecessorMigration);
            var company = new Company
            {
                Code = "SYN-CHART-MIGRATION",
                LegalName = "Synthetic pre-migration company",
                BaseCurrency = "TST"
            };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            var companyId = company.Id;

            await db.Database.MigrateAsync();
            (await db.Companies.SingleAsync(candidate => candidate.Id == companyId))
                .LegalName.Should().Be("Synthetic pre-migration company");
            (await db.ChartOfAccounts.CountAsync()).Should().Be(0,
                "the schema migration must not seed an unapproved chart");

            db.ChartOfAccounts.Add(new ChartOfAccount
            {
                CompanyId = companyId,
                AccountCode = "SYN-USER-SUPPLIED",
                Name = "Synthetic test account",
                AccountType = "Synthetic test label",
                IsGroupAccount = true
            });
            await db.SaveChangesAsync();
            (await db.ChartOfAccounts.SingleAsync()).AccountCode.Should().Be("SYN-USER-SUPPLIED");
        }
        finally
        {
            await using var dropSchema = new NpgsqlConnection(fixture.ConnectionString);
            await dropSchema.OpenAsync();
            await using var command = dropSchema.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<bool> WaitForChartDowngradeLockAsync(
        NpgsqlConnection connection,
        string schemaName,
        string applicationName,
        TimeSpan timeout)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_locks AS requested_lock
                JOIN pg_class AS relation ON relation.oid = requested_lock.relation
                JOIN pg_namespace AS relation_schema ON relation_schema.oid = relation.relnamespace
                JOIN pg_stat_activity AS activity ON activity.pid = requested_lock.pid
                WHERE activity.application_name = @applicationName
                  AND requested_lock.locktype = 'relation'
                  AND requested_lock.mode = 'AccessExclusiveLock'
                  AND NOT requested_lock.granted
                  AND relation_schema.nspname = @schemaName
                  AND relation.relname = 'ChartOfAccounts'
            )
            """,
            connection);
        command.Parameters.AddWithValue("applicationName", applicationName);
        command.Parameters.AddWithValue("schemaName", schemaName);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await command.ExecuteScalarAsync() is true)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    [PostgreSqlFact]
    public async Task Account_code_company_and_parent_constraints_are_enforced_by_postgresql()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var tenantId = $"chart-{suffix}";
        int companyAId;
        int companyBId;
        int companyBGroupId;

        await using (var seed = fixture.CreateContext(tenantId))
        {
            var companyA = new Company
            {
                Code = $"SYN-CHART-A-{suffix}",
                LegalName = "Synthetic chart company A",
                BaseCurrency = "TST"
            };
            var companyB = new Company
            {
                Code = $"SYN-CHART-B-{suffix}",
                LegalName = "Synthetic chart company B",
                BaseCurrency = "TST"
            };
            seed.AddRange(companyA, companyB);
            await seed.SaveChangesAsync();
            companyAId = companyA.Id;
            companyBId = companyB.Id;

            seed.ChartOfAccounts.AddRange(
                new ChartOfAccount
                {
                    CompanyId = companyAId,
                    AccountCode = "SYN-SHARED-CODE",
                    Name = "Synthetic company A group",
                    AccountType = "Synthetic test label",
                    IsGroupAccount = true
                },
                new ChartOfAccount
                {
                    CompanyId = companyBId,
                    AccountCode = "SYN-SHARED-CODE",
                    Name = "Synthetic company B group",
                    AccountType = "Synthetic test label",
                    IsGroupAccount = true
                });
            await seed.SaveChangesAsync();
            companyBGroupId = await seed.ChartOfAccounts
                .Where(account => account.CompanyId == companyBId)
                .Select(account => account.Id)
                .SingleAsync();
        }

        await using (var duplicate = fixture.CreateContext(tenantId))
        {
            duplicate.ChartOfAccounts.Add(new ChartOfAccount
            {
                CompanyId = companyAId,
                AccountCode = "SYN-SHARED-CODE",
                Name = "Synthetic duplicate",
                AccountType = "Synthetic test label"
            });
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
            exception.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }

        await using (var crossCompanyParent = fixture.CreateContext(tenantId))
        {
            crossCompanyParent.ChartOfAccounts.Add(new ChartOfAccount
            {
                CompanyId = companyAId,
                ParentAccountId = companyBGroupId,
                AccountCode = "SYN-CROSS-COMPANY-CHILD",
                Name = "Synthetic invalid child",
                AccountType = "Synthetic test label"
            });
            var exception = await Assert.ThrowsAsync<DbUpdateException>(
                () => crossCompanyParent.SaveChangesAsync());
            exception.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        }

        await using (var otherTenant = fixture.CreateContext($"other-{suffix}"))
        {
            (await otherTenant.ChartOfAccounts.CountAsync()).Should().Be(0,
                "the tenant query filter must hide another tenant's chart");
            otherTenant.ChartOfAccounts.Add(new ChartOfAccount
            {
                CompanyId = companyAId,
                AccountCode = "SYN-FOREIGN-TENANT",
                Name = "Synthetic invalid tenant reference",
                AccountType = "Synthetic test label"
            });
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => otherTenant.SaveChangesAsync());
            exception.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        }
    }
}
