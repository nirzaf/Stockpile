using FluentAssertions;
using Merconiq.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class CustomerMasterPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
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
