using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class ChartOfAccountsPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
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
