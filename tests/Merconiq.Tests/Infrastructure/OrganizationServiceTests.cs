using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Tests.Infrastructure;

public sealed class OrganizationServiceTests
{
    [Fact]
    public async Task GetCompanyAsync_does_not_return_a_tracked_company_from_another_tenant()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        context.Companies.Attach(new Company
        {
            Id = 42,
            TenantId = "tenant-b",
            Code = "FOREIGN",
            LegalName = "Foreign company"
        });
        var service = CreateService(context, "tenant-a");

        var company = await service.GetCompanyAsync(42);

        company.Should().BeNull();
    }

    [Fact]
    public async Task GetBranchesAsync_lists_inactive_company_branches_but_creation_stays_blocked()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var company = new Company { Code = "INACTIVE", LegalName = "Inactive company", IsActive = false };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        context.Branches.Add(new Branch
        {
            CompanyId = company.Id,
            Code = "OLD",
            Name = "Inactive branch",
            IsActive = false
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, "tenant-a");

        (await service.GetBranchesAsync(company.Id)).Should().ContainSingle(branch => branch.Code == "OLD");
        var act = () => service.CreateBranchAsync(new CreateBranchRequest(
            company.Id, "NEW", "New branch", null, "UTC"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The company does not exist in this tenant or is inactive.");
    }

    [Fact]
    public async Task Branch_cannot_be_activated_while_its_company_is_inactive()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var company = new Company { Code = "INACTIVE", LegalName = "Inactive company", IsActive = false };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        var branch = new Branch
        {
            CompanyId = company.Id,
            Code = "OLD",
            Name = "Inactive branch",
            IsActive = false
        };
        context.Branches.Add(branch);
        await context.SaveChangesAsync();
        var service = CreateService(context, "tenant-a");

        var act = () => service.UpdateBranchAsync(branch.Id,
            new UpdateBranchRequest(branch.Name, branch.Address, branch.TimeZoneId, IsActive: true));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The company does not exist in this tenant or is inactive.");
    }

    [Fact]
    public async Task Location_cannot_be_assigned_to_a_branch_of_an_inactive_company()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var company = new Company { Code = "INACTIVE", LegalName = "Inactive company", IsActive = false };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        var branch = new Branch
        {
            CompanyId = company.Id,
            Code = "BRANCH",
            Name = "Branch",
            IsActive = true
        };
        var location = new Location { Name = "Unmapped location" };
        context.Branches.Add(branch);
        context.Locations.Add(location);
        await context.SaveChangesAsync();
        var service = CreateService(context, "tenant-a");

        var act = () => service.AssignLocationBranchAsync(location.Id, branch.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The company does not exist in this tenant or is inactive.");
        location.BranchId.Should().BeNull();
    }

    private static OrganizationService CreateService(InventoryDbContext context, string tenantId) => new(
        new Repository<Company>(context),
        new Repository<Branch>(context),
        new Repository<Location>(context),
        new UnitOfWork(context),
        new TestTenantContext(tenantId));

    private static InventoryDbContext CreateContext(string databaseName, string tenantId)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        return new InventoryDbContext(options, new TestTenantContext(tenantId));
    }
}
