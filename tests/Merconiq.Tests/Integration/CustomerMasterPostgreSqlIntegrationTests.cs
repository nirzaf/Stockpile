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
public sealed class CustomerMasterPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Customer_migration_preserves_existing_company_data_from_the_previous_schema()
    {
        fixture.EnsureEnabled();
        const string predecessorMigration = "20260918183139_EnforceAuditLogAppendOnly";
        var schemaName = $"customer_migration_{Guid.NewGuid():N}";
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
                Code = "MIGRATION-COMPANY",
                LegalName = "Company created before customer migration",
                BaseCurrency = "USD"
            };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            var existingCompanyId = company.Id;

            await db.Database.MigrateAsync();
            (await db.Companies.SingleAsync(candidate => candidate.Id == existingCompanyId))
                .LegalName.Should().Be("Company created before customer migration");

            db.Customers.Add(new Customer
            {
                CompanyId = existingCompanyId,
                CustomerCode = "AFTER-MIGRATION",
                Name = "Customer created after migration"
            });
            await db.SaveChangesAsync();
            (await db.Customers.CountAsync()).Should().Be(1);
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
    public async Task Customer_keys_are_company_scoped_and_tenant_company_ownership_is_enforced()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var tenantId = $"customer-{suffix}";
        int companyAId;
        int companyBId;

        await using (var seed = fixture.CreateContext(tenantId))
        {
            var companyA = new Company
            {
                Code = $"CA-{suffix}",
                LegalName = "Synthetic customer company A",
                BaseCurrency = "USD"
            };
            var companyB = new Company
            {
                Code = $"CB-{suffix}",
                LegalName = "Synthetic customer company B",
                BaseCurrency = "USD"
            };
            seed.AddRange(companyA, companyB);
            await seed.SaveChangesAsync();
            companyAId = companyA.Id;
            companyBId = companyB.Id;

            seed.Customers.AddRange(
                new Customer { CompanyId = companyAId, CustomerCode = "SHARED-CODE", Name = "Company A customer" },
                new Customer { CompanyId = companyBId, CustomerCode = "SHARED-CODE", Name = "Company B customer" });
            await seed.SaveChangesAsync();
        }

        await using (var duplicate = fixture.CreateContext(tenantId))
        {
            duplicate.Customers.Add(new Customer
            {
                CompanyId = companyAId,
                CustomerCode = "SHARED-CODE",
                Name = "Duplicate in company A"
            });
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
            exception.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }

        await using (var otherTenant = fixture.CreateContext($"other-{suffix}"))
        {
            (await otherTenant.Customers.CountAsync()).Should().Be(0,
                "the tenant query filter hides customer rows owned by another tenant");

            otherTenant.Customers.Add(new Customer
            {
                CompanyId = companyAId,
                CustomerCode = "FOREIGN-COMPANY",
                Name = "Invalid tenant-company pair"
            });
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => otherTenant.SaveChangesAsync());
            exception.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        }

        await using var companyDelete = fixture.CreateContext(tenantId);
        var referencedCompany = await companyDelete.Companies.SingleAsync(company => company.Id == companyAId);
        companyDelete.Companies.Remove(referencedCompany);
        var deleteException = await Assert.ThrowsAsync<DbUpdateException>(() => companyDelete.SaveChangesAsync());
        deleteException.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation,
                "a customer record must not outlive or become detached from its owning company");
    }
}
