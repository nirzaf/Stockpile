using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.Controllers.Api.V1;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

public sealed class CompanyCapabilityAuthorizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string TenantId = "test-tenant";
    private readonly CustomWebApplicationFactory _factory;

    public CompanyCapabilityAuthorizationTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Six_persona_matrix_requires_both_role_ceiling_and_company_grant()
    {
        var company = await CreateCompanyAsync();
        var cases = new[]
        {
            ("Operator", CompanyCapability.View | CompanyCapability.Post),
            ("Buyer", CompanyCapability.View | CompanyCapability.Edit),
            ("Accountant", CompanyCapability.View | CompanyCapability.Approve |
                CompanyCapability.Post | CompanyCapability.Reverse | CompanyCapability.OverrideExpiredStock),
            ("Cashier", CompanyCapability.View | CompanyCapability.Post),
            ("CompanyAdmin", CompanyCapability.View | CompanyCapability.Edit | CompanyCapability.Administer),
            ("RestrictedAuditor", CompanyCapability.View)
        };

        using var authorizationScope = _factory.Services.CreateScope();
        var authorization = authorizationScope.ServiceProvider.GetRequiredService<ICurrentUserAuthorization>();
        var roles = authorizationScope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = authorizationScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        foreach (var (role, grant) in cases)
        {
            using var client = _factory.CreateAuthenticatedClient(role);
            var user = await users.FindByNameAsync($"{role.ToLowerInvariant()}@{TenantId}.test");
            user.Should().NotBeNull();
            (await roles.RoleExistsAsync(role)).Should().BeTrue();
            await AddMembershipAsync(company.Id, user!.Id, grant);

            var principal = CreatePrincipal(user, role);
            foreach (var capability in Enum.GetValues<CompanyCapability>().Where(value => value != CompanyCapability.None))
            {
                var roleCanPerform = RoleCanPerform(role, capability);
                var grantIncludesCapability = (grant & capability) == capability;
                var actual = await authorization.CanAccessCompanyAsync(principal, company.Id, capability);

                actual.Should().Be(roleCanPerform && grantIncludesCapability,
                    $"{role} with {grant} should {(roleCanPerform && grantIncludesCapability ? "have" : "not have")} {capability}");
            }
        }
    }

    [Fact]
    public async Task Expired_stock_override_requires_a_company_grant_and_persists_its_reason()
    {
        var company = await CreateCompanyAsync();
        var (location, _, item) = await CreateStockFixtureAsync(company.Id, company.Id);
        var expiryDate = DateTime.UtcNow.Date.AddDays(-1);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var stock = await db.StockInHand.SingleAsync(row => row.LocationId == location.Id);
            stock.BatchNumber = "LOT-EXPIRED-AUTH";
            stock.ExpiryDate = expiryDate;
            await db.SaveChangesAsync();
        }

        using var operatorClient = _factory.CreateAuthenticatedClient("Operator");
        using var accountantBootstrapClient = _factory.CreateAuthenticatedClient("Accountant");
        var operatorUser = await GetTestUserAsync("Operator");
        var accountant = await GetTestUserAsync("Accountant");
        await ClearMembershipsAsync(operatorUser.Id);
        await ClearMembershipsAsync(accountant.Id);
        await AddMembershipAsync(company.Id, operatorUser.Id, CompanyCapability.View | CompanyCapability.Post);
        using (var scope = _factory.Services.CreateScope())
        {
            var memberships = scope.ServiceProvider.GetRequiredService<ICompanyMembershipService>();
            await memberships.SetCapabilitiesAsync(company.Id, accountant.Id,
                CompanyCapability.View | CompanyCapability.Post | CompanyCapability.OverrideExpiredStock);
        }
        using var accountantClient = _factory.CreateAuthenticatedClient("Accountant");

        const string reason = "Approved exception for controlled disposal";
        var request = new
        {
            itemId = item.Id,
            locationId = location.Id,
            quantity = 1,
            batchNumber = "LOT-EXPIRED-AUTH",
            expiryDate,
            expiryExceptionReason = reason
        };

        var denied = await operatorClient.PostAsJsonAsync("/api/v1/stock/sell", request);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            (await db.StockInHand.SingleAsync(row => row.LocationId == location.Id)).Quantity.Should().Be(3);
            (await db.StockTransactions.CountAsync(row => row.ItemId == item.Id)).Should().Be(0);
        }

        var allowed = await accountantClient.PostAsJsonAsync("/api/v1/stock/sell", request);
        allowed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            (await db.StockInHand.SingleAsync(row => row.LocationId == location.Id)).Quantity.Should().Be(2);
            var movement = await db.StockTransactions.SingleAsync(row => row.ItemId == item.Id);
            movement.ExpiryExceptionReason.Should().Be(reason);
            var audit = await db.AuditLogs.SingleAsync(row =>
                row.EntityName == nameof(StockTransaction) && row.NewValues!.Contains(reason));
            audit.NewValues.Should().Contain(nameof(StockTransaction.ExpiryExceptionReason));
        }

        await ClearMembershipsAsync(operatorUser.Id);
        await ClearMembershipsAsync(accountant.Id);
    }

    [Fact]
    public async Task Company_reads_and_stock_lists_are_filtered_to_current_memberships()
    {
        using var client = _factory.CreateAuthenticatedClient("Operator");
        var user = await GetTestUserAsync("Operator");
        var first = await CreateCompanyAsync();
        var second = await CreateCompanyAsync();
        var (firstLocation, secondLocation, item) = await CreateStockFixtureAsync(first.Id, second.Id);
        await AddMembershipAsync(first.Id, user.Id, CompanyCapability.View | CompanyCapability.Post);

        var companiesResponse = await client.GetAsync("/api/v1/organization/companies");
        companiesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var companies = await companiesResponse.Content.ReadFromJsonAsync<ApiResponse<List<CompanyResponse>>>();
        companies!.Data.Should().ContainSingle(company => company.Id == first.Id);

        var deniedCompany = await client.GetAsync($"/api/v1/organization/companies/{second.Id}");
        deniedCompany.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var stockResponse = await client.GetAsync("/api/v1/stock/in-hand");
        stockResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var stock = await stockResponse.Content.ReadFromJsonAsync<ApiResponse<List<StockInHand>>>();
        (stock?.Data ?? []).Select(row => row.LocationId).Should().Equal(firstLocation.Id);

        var deniedPost = await client.PostAsJsonAsync("/api/v1/stock/receive", new
        {
            ItemId = item.Id,
            LocationId = secondLocation.Id,
            Quantity = 1,
            Notes = "must not cross company boundary"
        });
        deniedPost.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        (await db.StockInHand.CountAsync(row => row.LocationId == secondLocation.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Shared_item_api_requires_membership_and_rechecks_revocation()
    {
        using var client = _factory.CreateAuthenticatedClient("Operator");
        var user = await GetTestUserAsync("Operator");
        var company = await CreateCompanyAsync();
        await ClearMembershipsAsync(user.Id);

        (await client.GetAsync("/api/v1/items")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await AddMembershipAsync(company.Id, user.Id,
            CompanyCapability.View | CompanyCapability.Post);
        (await client.GetAsync("/api/v1/items")).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var membership = await db.CompanyMemberships.SingleAsync(grant =>
            grant.CompanyId == company.Id && grant.UserId == user.Id);
        membership.IsActive = false;
        await db.SaveChangesAsync();

        (await client.GetAsync("/api/v1/items")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Membership_change_revokes_existing_jwt_security_stamp()
    {
        using var client = _factory.CreateAuthenticatedClient("Buyer");
        var user = await GetTestUserAsync("Buyer");
        var company = await CreateCompanyAsync();
        await ClearMembershipsAsync(user.Id);

        (await client.GetAsync("/api/v1/items")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var scope = _factory.Services.CreateScope();
        var memberships = scope.ServiceProvider.GetRequiredService<ICompanyMembershipService>();
        await memberships.SetCapabilitiesAsync(company.Id, user.Id,
            CompanyCapability.View | CompanyCapability.Edit);

        (await client.GetAsync("/api/v1/items")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Membership_revocation_is_seen_by_the_same_principal_on_the_next_authorization_check()
    {
        using var client = _factory.CreateAuthenticatedClient("Buyer");
        var user = await GetTestUserAsync("Buyer");
        var company = await CreateCompanyAsync();
        await AddMembershipAsync(company.Id, user.Id, CompanyCapability.View | CompanyCapability.Edit);

        using var scope = _factory.Services.CreateScope();
        var authorization = scope.ServiceProvider.GetRequiredService<ICurrentUserAuthorization>();
        var principal = CreatePrincipal(user, "Buyer");
        (await authorization.CanAccessCompanyAsync(principal, company.Id, CompanyCapability.Edit)).Should().BeTrue();
        (await authorization.GetAccessibleCompanyIdsAsync(principal, CompanyCapability.View))
            .Should().Contain(company.Id);

        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var membership = await db.CompanyMemberships.SingleAsync(grant =>
            grant.CompanyId == company.Id && grant.UserId == user.Id);
        membership.IsActive = false;
        await db.SaveChangesAsync();

        (await authorization.CanAccessCompanyAsync(principal, company.Id, CompanyCapability.Edit)).Should().BeFalse();
        (await authorization.GetAccessibleCompanyIdsAsync(principal, CompanyCapability.View))
            .Should().NotContain(company.Id);
    }

    [Fact]
    public async Task Location_creation_and_reassignment_require_an_active_authorized_branch()
    {
        using var client = _factory.CreateAuthenticatedClient("CompanyAdmin");
        var user = await GetTestUserAsync("CompanyAdmin");
        await ClearMembershipsAsync(user.Id);
        var company = await CreateCompanyAsync();
        await AddMembershipAsync(company.Id, user.Id,
            CompanyCapability.View | CompanyCapability.Edit | CompanyCapability.Administer);

        Branch activeBranch;
        Branch inactiveBranch;
        Location location;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            activeBranch = new Branch
            {
                CompanyId = company.Id,
                Code = $"ACTIVE-{Guid.NewGuid():N}"[..16],
                Name = "Active branch"
            };
            inactiveBranch = new Branch
            {
                CompanyId = company.Id,
                Code = $"INACTIVE-{Guid.NewGuid():N}"[..18],
                Name = "Inactive branch",
                IsActive = false
            };
            db.Branches.AddRange(activeBranch, inactiveBranch);
            await db.SaveChangesAsync();
            location = new Location { Name = "Existing location", BranchId = activeBranch.Id };
            db.Locations.Add(location);
            await db.SaveChangesAsync();
        }

        using var authorizationScope = _factory.Services.CreateScope();
        var authorization = authorizationScope.ServiceProvider.GetRequiredService<ICurrentUserAuthorization>();
        var principal = CreatePrincipal(user, "CompanyAdmin");

        (await authorization.CanCreateLocationInBranchAsync(principal, activeBranch.Id)).Should().BeTrue();
        (await authorization.CanCreateLocationInBranchAsync(principal, inactiveBranch.Id)).Should().BeFalse();
        (await authorization.CanAssignLocationBranchAsync(principal, location.Id, inactiveBranch.Id)).Should().BeFalse();
        (await authorization.CanAssignLocationBranchAsync(principal, location.Id, activeBranch.Id)).Should().BeTrue();
    }

    private async Task<Company> CreateCompanyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var company = new Company
        {
            Code = $"CC-{Guid.NewGuid():N}".Substring(0, 10),
            LegalName = "Authorization test company"
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company;
    }

    private async Task AddMembershipAsync(int companyId, string userId, CompanyCapability capabilities)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        db.CompanyMemberships.Add(new CompanyMembership
        {
            CompanyId = companyId,
            UserId = userId,
            Capabilities = capabilities,
            IsActive = true
        });
        await db.SaveChangesAsync();
    }

    private async Task ClearMembershipsAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var memberships = await db.CompanyMemberships.Where(grant => grant.UserId == userId).ToListAsync();
        db.CompanyMemberships.RemoveRange(memberships);
        await db.SaveChangesAsync();
    }

    private async Task<ApplicationUser> GetTestUserAsync(string role)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await userManager.FindByNameAsync($"{role.ToLowerInvariant()}@{TenantId}.test"))!;
    }

    private async Task<(Location firstLocation, Location secondLocation, Item item)> CreateStockFixtureAsync(
        int firstCompanyId,
        int secondCompanyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var firstBranch = new Branch
        {
            CompanyId = firstCompanyId,
            Code = $"B1-{Guid.NewGuid():N}".Substring(0, 10),
            Name = "First branch"
        };
        var secondBranch = new Branch
        {
            CompanyId = secondCompanyId,
            Code = $"B2-{Guid.NewGuid():N}".Substring(0, 10),
            Name = "Second branch"
        };
        db.Branches.AddRange(firstBranch, secondBranch);
        await db.SaveChangesAsync();
        var firstLocation = new Location { Name = "First warehouse", BranchId = firstBranch.Id };
        var secondLocation = new Location { Name = "Second warehouse", BranchId = secondBranch.Id };
        var item = new Item
        {
            ItemCode = $"AUTH-{Guid.NewGuid():N}".Substring(0, 16),
            Description = "Authorization fixture",
            Rate = 1m
        };
        db.AddRange(firstLocation, secondLocation, item);
        await db.SaveChangesAsync();
        db.StockInHand.AddRange(
            new StockInHand { ItemId = item.Id, LocationId = firstLocation.Id, Quantity = 3 },
            new StockInHand { ItemId = item.Id, LocationId = secondLocation.Id, Quantity = 7 });
        await db.SaveChangesAsync();
        return (firstLocation, secondLocation, item);
    }

    private static ClaimsPrincipal CreatePrincipal(ApplicationUser user, string role) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.UserName ?? string.Empty),
            new Claim(ClaimTypes.Role, role),
            new Claim("tenant_id", TenantId),
            new Claim("AspNet.Identity.SecurityStamp", user.SecurityStamp ?? string.Empty)
        ], "company-capability-test"));

    private static bool RoleCanPerform(string role, CompanyCapability capability) => capability switch
    {
        CompanyCapability.View => true,
        CompanyCapability.Edit => role is "Buyer" or "CompanyAdmin",
        CompanyCapability.Approve => role == "Accountant",
        CompanyCapability.Post => role is "Operator" or "Accountant" or "Cashier",
        CompanyCapability.Reverse => role == "Accountant",
        CompanyCapability.OverrideExpiredStock => role == "Accountant",
        CompanyCapability.Administer => role == "CompanyAdmin",
        _ => false
    };
}
