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
    public void Company_does_not_assume_a_jurisdiction_specific_base_currency()
    {
        new Company().BaseCurrency.Should().BeEmpty();
        new Company().CurrencyScale.Should().BeNull();
    }

    [Fact]
    public async Task CreateCompany_requires_explicit_currency_scale_and_accepts_zero()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var service = CreateService(context, "tenant-a");

        var missing = () => service.CreateCompanyAsync(new CreateCompanyRequest(
            "COMPANY", "Company", null, null, null, "USD", null));

        await missing.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Currency scale must be supplied and must be between 0 and 4.");

        var company = await service.CreateCompanyAsync(new CreateCompanyRequest(
            "COMPANY", "Company", null, null, null, "JPY", null, CurrencyScale: 0));

        company.CurrencyScale.Should().Be(0);
        company.BaseCurrency.Should().Be("JPY");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public async Task CreateCompany_rejects_currency_scale_outside_supported_range(int scale)
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var service = CreateService(context, "tenant-a");

        var act = () => service.CreateCompanyAsync(new CreateCompanyRequest(
            "COMPANY", "Company", null, null, null, "USD", null, scale));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Currency scale must be supplied and must be between 0 and 4.");
    }

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

    [Fact]
    public async Task Location_branch_ownership_cannot_change_after_posted_stock_activity()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var company = new Company { Code = "COMPANY", LegalName = "Company" };
        var originalBranch = new Branch { Company = company, Code = "ORIGINAL", Name = "Original" };
        var newBranch = new Branch { Company = company, Code = "NEW", Name = "New" };
        var location = new Location { Branch = originalBranch, Name = "Warehouse" };
        context.StockTransactions.Add(new StockTransaction { FromLocation = location, Quantity = 1 });
        context.Branches.Add(newBranch);
        await context.SaveChangesAsync();
        var service = CreateService(context, "tenant-a");

        var act = () => service.AssignLocationBranchAsync(location.Id, newBranch.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A location's branch ownership cannot change after posted stock activity.");
        (await context.Locations.SingleAsync()).BranchId.Should().Be(originalBranch.Id);
    }

    [Fact]
    public async Task Unmapped_legacy_location_can_be_assigned_after_posted_stock_activity()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var company = new Company { Code = "COMPANY", LegalName = "Company" };
        var branch = new Branch { Company = company, Code = "BRANCH", Name = "Branch" };
        var location = new Location { Name = "Legacy warehouse" };
        context.StockTransactions.Add(new StockTransaction { FromLocation = location, Quantity = 1 });
        context.Branches.Add(branch);
        await context.SaveChangesAsync();
        var service = CreateService(context, "tenant-a");

        await service.AssignLocationBranchAsync(location.Id, branch.Id);

        (await context.Locations.SingleAsync()).BranchId.Should().Be(branch.Id);
    }

    [Fact]
    public async Task Company_base_currency_cannot_change_after_stock_activity()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var company = new Company { Code = "COMPANY", LegalName = "Company", BaseCurrency = "QAR", CurrencyScale = 2 };
        var branch = new Branch { Company = company, Code = "BRANCH", Name = "Branch" };
        var location = new Location { Branch = branch, Name = "Warehouse" };
        context.StockTransactions.Add(new StockTransaction { FromLocation = location, Quantity = 1 });
        await context.SaveChangesAsync();
        var service = CreateService(context, "tenant-a");

        var act = () => service.UpdateCompanyAsync(company.Id,
            new UpdateCompanyRequest(company.LegalName, null, null, null, "USD", null, true, CurrencyScale: 2));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A company's base currency cannot change after posted stock activity.");
        (await context.Companies.SingleAsync()).BaseCurrency.Should().Be("QAR");
    }

    [Fact]
    public async Task Company_base_currency_can_change_before_stock_activity()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var company = new Company { Code = "COMPANY", LegalName = "Company", BaseCurrency = "QAR", CurrencyScale = 2 };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        var service = CreateService(context, "tenant-a");

        await service.UpdateCompanyAsync(company.Id,
            new UpdateCompanyRequest(company.LegalName, null, null, null, "USD", null, true, CurrencyScale: 0));

        var updated = await context.Companies.SingleAsync();
        updated.BaseCurrency.Should().Be("USD");
        updated.CurrencyScale.Should().Be(0);
    }

    [Fact]
    public async Task Company_currency_scale_cannot_change_after_stock_activity()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var company = new Company { Code = "COMPANY", LegalName = "Company", BaseCurrency = "USD", CurrencyScale = 2 };
        var branch = new Branch { Company = company, Code = "BRANCH", Name = "Branch" };
        var location = new Location { Branch = branch, Name = "Warehouse" };
        context.StockTransactions.Add(new StockTransaction { FromLocation = location, Quantity = 1 });
        await context.SaveChangesAsync();
        var service = CreateService(context, "tenant-a");

        var act = () => service.UpdateCompanyAsync(company.Id,
            new UpdateCompanyRequest(company.LegalName, null, null, null, "USD", null, true, CurrencyScale: 3));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("A company's currency scale cannot change after posted stock activity.");
        (await context.Companies.SingleAsync()).CurrencyScale.Should().Be(2);
    }

    [Fact]
    public async Task Company_update_without_currency_scale_preserves_existing_scale()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var company = new Company { Code = "COMPANY", LegalName = "Company", BaseCurrency = "USD", CurrencyScale = 2 };
        context.Companies.Add(company);
        await context.SaveChangesAsync();
        var service = CreateService(context, "tenant-a");

        await service.UpdateCompanyAsync(company.Id,
            new UpdateCompanyRequest("Renamed company", null, null, null, "USD", null, true));

        var updated = await context.Companies.SingleAsync();
        updated.LegalName.Should().Be("Renamed company");
        updated.CurrencyScale.Should().Be(2);
    }

    [Fact]
    public async Task Legacy_company_currency_scale_can_be_initialized_after_stock_activity()
    {
        await using var context = CreateContext(Guid.NewGuid().ToString(), "tenant-a");
        var company = new Company { Code = "COMPANY", LegalName = "Company", BaseCurrency = "USD" };
        var branch = new Branch { Company = company, Code = "BRANCH", Name = "Branch" };
        var location = new Location { Branch = branch, Name = "Warehouse" };
        context.StockTransactions.Add(new StockTransaction { FromLocation = location, Quantity = 1 });
        await context.SaveChangesAsync();
        var service = CreateService(context, "tenant-a");

        await service.UpdateCompanyAsync(company.Id,
            new UpdateCompanyRequest(company.LegalName, null, null, null, "USD", null, true, CurrencyScale: 2));

        (await context.Companies.SingleAsync()).CurrencyScale.Should().Be(2);
    }

    private static OrganizationService CreateService(InventoryDbContext context, string tenantId) => new(
        new Repository<Company>(context),
        new Repository<Branch>(context),
        new Repository<Location>(context),
        new UnitOfWork(context),
        new TestTenantContext(tenantId),
        context);

    private static InventoryDbContext CreateContext(string databaseName, string tenantId)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        return new InventoryDbContext(options, new TestTenantContext(tenantId));
    }
}
