using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Infrastructure;

public class OrganizationOwnershipTests
{
    [Fact]
    public async Task Company_and_branch_queries_are_tenant_scoped()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using (var tenantA = CreateContext(databaseName, "tenant-a"))
        {
            tenantA.Companies.Add(new Company { Code = "A", LegalName = "Tenant A Company" });
            await tenantA.SaveChangesAsync();
        }

        await using (var tenantB = CreateContext(databaseName, "tenant-b"))
        {
            tenantB.Companies.Add(new Company { Code = "B", LegalName = "Tenant B Company" });
            await tenantB.SaveChangesAsync();
        }

        await using var readA = CreateContext(databaseName, "tenant-a");
        (await readA.Companies.ToListAsync()).Should().ContainSingle(c => c.Code == "A");
        (await readA.Companies.ToListAsync()).Should().NotContain(c => c.Code == "B");
    }

    [Fact]
    public void Organization_model_requires_tenant_scoped_company_and_branch_keys()
    {
        using var context = CreateContext(Guid.NewGuid().ToString(), "default");
        var company = context.Model.FindEntityType(typeof(Company))!;
        var branch = context.Model.FindEntityType(typeof(Branch))!;
        var companyIndex = company.GetIndexes().Single(i => i.Properties.Select(p => p.Name)
            .SequenceEqual(new[] { nameof(Company.TenantId), nameof(Company.Code) }));
        var branchIndex = branch.GetIndexes().Single(i => i.Properties.Select(p => p.Name)
            .SequenceEqual(new[] { nameof(Branch.TenantId), nameof(Branch.CompanyId), nameof(Branch.Code) }));

        companyIndex.IsUnique.Should().BeTrue();
        branchIndex.IsUnique.Should().BeTrue();
        branch.GetForeignKeys().Should().ContainSingle(fk =>
            fk.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(Branch.CompanyId), nameof(Branch.TenantId) }));

        var stock = context.Model.FindEntityType(typeof(StockInHand))!;
        stock.GetForeignKeys().Should().Contain(fk =>
            fk.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(StockInHand.LocationId), nameof(StockInHand.TenantId) }) &&
            fk.PrincipalEntityType.ClrType == typeof(Location));

        var transaction = context.Model.FindEntityType(typeof(StockTransaction))!;
        transaction.GetForeignKeys().Should().Contain(fk =>
            fk.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(StockTransaction.FromLocationId), nameof(StockTransaction.TenantId) }) &&
            fk.PrincipalEntityType.ClrType == typeof(Location));
        transaction.GetForeignKeys().Should().Contain(fk =>
            fk.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(StockTransaction.ToLocationId), nameof(StockTransaction.TenantId) }) &&
            fk.PrincipalEntityType.ClrType == typeof(Location));
    }

    private static InventoryDbContext CreateContext(string databaseName, string tenantId)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        return new InventoryDbContext(options, new TestTenantContext(tenantId));
    }
}
