using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

public sealed class QuarantinedStockAuthorizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string TenantId = "test-tenant";
    private readonly CustomWebApplicationFactory _factory;

    public QuarantinedStockAuthorizationTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Quarantine_release_requires_a_role_ceiling_and_explicit_company_grant()
    {
        var (company, location, otherCompany) = await CreateFixtureAsync();
        using var adminClient = _factory.CreateAuthenticatedClient("Admin");
        using var accountantClient = _factory.CreateAuthenticatedClient("Accountant");
        using var operatorClient = _factory.CreateAuthenticatedClient("Operator");
        var admin = await GetUserAsync("Admin");
        var accountant = await GetUserAsync("Accountant");
        var operatorUser = await GetUserAsync("Operator");
        await ClearMembershipsAsync(admin.Id);
        await ClearMembershipsAsync(accountant.Id);
        await ClearMembershipsAsync(operatorUser.Id);

        using var scope = _factory.Services.CreateScope();
        var authorization = scope.ServiceProvider.GetRequiredService<ICurrentUserAuthorization>();

        var adminPrincipal = Principal(admin, "Admin");
        (await authorization.CanOverrideQuarantinedStockAtLocationAsync(adminPrincipal, location.Id))
            .Should().BeFalse("tenant administrators still need an explicit grant for quarantine release");

        await AddMembershipAsync(company.Id, admin.Id, CompanyCapability.Post);
        (await authorization.CanOverrideQuarantinedStockAtLocationAsync(adminPrincipal, location.Id))
            .Should().BeFalse("a normal posting grant is not a quarantine-release grant");

        await SetCapabilitiesAsync(company.Id, admin.Id, CompanyCapability.OverrideQuarantinedStock);
        (await authorization.CanOverrideQuarantinedStockAtLocationAsync(adminPrincipal, location.Id))
            .Should().BeTrue();
        (await authorization.CanAccessCompanyAsync(
            adminPrincipal, otherCompany.Id, CompanyCapability.OverrideQuarantinedStock)).Should().BeFalse();
        (await authorization.GetAccessibleCompanyIdsAsync(
            adminPrincipal, CompanyCapability.OverrideQuarantinedStock)).Should().Equal(company.Id);

        await SetMembershipThroughServiceAsync(company.Id, accountant.Id,
            CompanyCapability.OverrideQuarantinedStock);
        accountant = await GetUserAsync("Accountant");
        (await authorization.CanOverrideQuarantinedStockAtLocationAsync(
            Principal(accountant, "Accountant"), location.Id)).Should().BeTrue();

        await AddMembershipAsync(company.Id, operatorUser.Id, CompanyCapability.OverrideQuarantinedStock);
        (await authorization.CanOverrideQuarantinedStockAtLocationAsync(
            Principal(operatorUser, "Operator"), location.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Quarantine_api_is_idempotent_and_release_requires_override_without_changing_on_hand()
    {
        var (company, location, _) = await CreateFixtureAsync();
        var (item, initialQuantity) = await CreateStockAsync(location.Id);
        using var operatorClient = _factory.CreateAuthenticatedClient("Operator");
        using var accountantClient = _factory.CreateAuthenticatedClient("Accountant");
        var operatorUser = await GetUserAsync("Operator");
        var accountant = await GetUserAsync("Accountant");
        await ClearMembershipsAsync(operatorUser.Id);
        await ClearMembershipsAsync(accountant.Id);
        await AddMembershipAsync(company.Id, operatorUser.Id, CompanyCapability.Post);
        await AddMembershipAsync(company.Id, accountant.Id, CompanyCapability.Post);

        var quarantineRequest = new
        {
            itemId = item.Id,
            locationId = location.Id,
            quantity = 2,
            sourceLineReference = $"quarantine-{Guid.NewGuid():N}",
            reason = "Hold for inspection"
        };

        (await operatorClient.PostAsJsonAsync("/api/v1/stock/quarantine", quarantineRequest))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await operatorClient.PostAsJsonAsync("/api/v1/stock/quarantine", quarantineRequest))
            .StatusCode.Should().Be(HttpStatusCode.NoContent, "the source line makes the operation replay-safe");

        var releaseRequest = new
        {
            itemId = item.Id,
            locationId = location.Id,
            quantity = 1,
            sourceLineReference = $"release-{Guid.NewGuid():N}",
            reason = "Inspection completed"
        };
        (await accountantClient.PostAsJsonAsync("/api/v1/stock/quarantine/release", releaseRequest))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "posting authority alone must not permit a quarantined-stock release");

        await SetCapabilitiesAsync(company.Id, accountant.Id,
            CompanyCapability.Post | CompanyCapability.OverrideQuarantinedStock);
        (await accountantClient.PostAsJsonAsync("/api/v1/stock/quarantine/release", releaseRequest))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var stock = await db.StockInHand.SingleAsync(row => row.ItemId == item.Id && row.LocationId == location.Id);
        stock.Quantity.Should().Be(initialQuantity);
        stock.QuarantinedQuantity.Should().Be(1);
        var movements = await db.StockTransactions.Where(row => row.ItemId == item.Id).ToListAsync();
        movements.Should().HaveCount(2);
        movements.Should().ContainSingle(row => row.QuarantineReason == "Hold for inspection");
        movements.Should().ContainSingle(row => row.QuarantineReason == "Inspection completed");
    }

    private async Task<(Company company, Location location, Company otherCompany)> CreateFixtureAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var company = new Company
        {
            Code = $"QZ-{Guid.NewGuid():N}"[..10],
            LegalName = "Quarantine authorization company"
        };
        var otherCompany = new Company
        {
            Code = $"QZ-{Guid.NewGuid():N}"[..10],
            LegalName = "Other quarantine authorization company"
        };
        db.Companies.AddRange(company, otherCompany);
        await db.SaveChangesAsync();
        var branch = new Branch
        {
            CompanyId = company.Id,
            Code = $"QZB-{Guid.NewGuid():N}"[..12],
            Name = "Quarantine branch"
        };
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        var location = new Location { Name = "Quarantine location", BranchId = branch.Id };
        db.Locations.Add(location);
        await db.SaveChangesAsync();
        return (company, location, otherCompany);
    }

    private async Task<(Item item, int quantity)> CreateStockAsync(int locationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var item = new Item
        {
            ItemCode = $"QZ-{Guid.NewGuid():N}"[..16],
            Description = "Quarantine authorization stock",
            Rate = 1m
        };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        const int quantity = 5;
        db.StockInHand.Add(new StockInHand { ItemId = item.Id, LocationId = locationId, Quantity = quantity });
        await db.SaveChangesAsync();
        return (item, quantity);
    }

    private async Task<ApplicationUser> GetUserAsync(string role)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByNameAsync($"{role.ToLowerInvariant()}@{TenantId}.test"))!;
    }

    private async Task AddMembershipAsync(int companyId, string userId, CompanyCapability capabilities)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var membership = await db.CompanyMemberships.SingleOrDefaultAsync(grant =>
            grant.CompanyId == companyId && grant.UserId == userId);
        if (membership is null)
        {
            db.CompanyMemberships.Add(new CompanyMembership
            {
                CompanyId = companyId,
                UserId = userId,
                Capabilities = capabilities,
                IsActive = true
            });
        }
        else
        {
            membership.Capabilities = capabilities;
            membership.IsActive = true;
        }

        await db.SaveChangesAsync();
    }

    private Task SetCapabilitiesAsync(int companyId, string userId, CompanyCapability capabilities) =>
        AddMembershipAsync(companyId, userId, capabilities);

    private async Task SetMembershipThroughServiceAsync(
        int companyId,
        string userId,
        CompanyCapability capabilities)
    {
        using var scope = _factory.Services.CreateScope();
        var memberships = scope.ServiceProvider.GetRequiredService<ICompanyMembershipService>();
        await memberships.SetCapabilitiesAsync(companyId, userId, capabilities);
    }

    private async Task ClearMembershipsAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var grants = await db.CompanyMemberships.Where(grant => grant.UserId == userId).ToListAsync();
        db.CompanyMemberships.RemoveRange(grants);
        await db.SaveChangesAsync();
    }

    private static ClaimsPrincipal Principal(ApplicationUser user, string role) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id),
        new Claim(ClaimTypes.Name, user.UserName ?? string.Empty),
        new Claim(ClaimTypes.Role, role),
        new Claim("tenant_id", TenantId),
        new Claim("AspNet.Identity.SecurityStamp", user.SecurityStamp ?? string.Empty)
    ], "quarantine-authorization-test"));
}
