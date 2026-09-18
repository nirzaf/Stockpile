using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.Security;
using Merconiq.Web.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class CompanyCapabilityPostgreSqlApiTests(PostgreSqlIntegrationFixture fixture) : IDisposable
{
    private readonly PostgreSqlCompanyApiFactory _factory = new(fixture);

    private static async Task<HttpResponseMessage> PostStockReceiveAsync(
        HttpClient client,
        object command,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/stock/receive")
        {
            Content = JsonContent.Create(command)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? $"capability-receive-{Guid.NewGuid():N}");
        return await client.SendAsync(request);
    }

    [PostgreSqlFact]
    public async Task Real_jwt_can_import_branches_only_for_a_granted_company()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = await SeedCompanyStockAsync(fixture, suffix);
        var buyer = await CreatePersonaAsync(
            "Buyer", CompanyCapability.View | CompanyCapability.Edit, suffix, tenant.CompanyAId);

        const string authorizedCsv = "external_id,code,name,address,time_zone_id,is_active\n"
            + "branch-a,BR-A,Authorized branch,,UTC,true";
        var authorizedResponse = await buyer.Client.PostAsJsonAsync(
            "/api/v1/organization/branches/import",
            new { csv = authorizedCsv, dryRun = false, companyId = tenant.CompanyAId });

        authorizedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var authorizedBody = await authorizedResponse.Content.ReadFromJsonAsync<JsonDocument>())
        {
            authorizedBody!.RootElement.GetProperty("data").GetProperty("created").GetInt32().Should().Be(1);
        }

        const string forbiddenCsv = "external_id,code,name,address,time_zone_id,is_active\n"
            + "branch-b,BR-B,Unauthorized branch,,UTC,true";
        var forbiddenResponse = await buyer.Client.PostAsJsonAsync(
            "/api/v1/organization/branches/import",
            new { csv = forbiddenCsv, dryRun = false, companyId = tenant.CompanyBId });

        forbiddenResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await using (var revoke = fixture.CreateContext(tenant.TenantId))
        {
            var membership = await revoke.CompanyMemberships.SingleAsync(candidate =>
                candidate.CompanyId == tenant.CompanyAId && candidate.UserId == buyer.User.Id);
            membership.IsActive = false;
            await revoke.SaveChangesAsync();
        }

        const string revokedCsv = "external_id,code,name,address,time_zone_id,is_active\n"
            + "branch-revoked,BR-REVOKED,Revoked branch,,UTC,true";
        var revokedResponse = await buyer.Client.PostAsJsonAsync(
            "/api/v1/organization/branches/import",
            new { csv = revokedCsv, dryRun = false, companyId = tenant.CompanyAId });
        revokedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the same still-valid JWT must lose Edit access as soon as its company membership is revoked");

        await using var verification = fixture.CreateContext(tenant.TenantId);
        var companyABranches = await verification.Branches.IgnoreQueryFilters()
            .Where(branch => branch.TenantId == tenant.TenantId &&
                (branch.ExternalId == "branch-a" || branch.ExternalId == "branch-b" ||
                 branch.ExternalId == "branch-revoked"))
            .Select(branch => new { branch.ExternalId, branch.CompanyId })
            .ToListAsync();
        companyABranches.Should().ContainSingle();
        companyABranches[0].ExternalId.Should().Be("branch-a");
        companyABranches[0].CompanyId.Should().Be(tenant.CompanyAId);

        (await verification.Branches.IgnoreQueryFilters()
            .CountAsync(branch => branch.TenantId == tenant.TenantId && branch.ExternalId == "branch-b"))
            .Should().Be(0, "a denied import must not create a branch for the ungranted company");
        (await verification.Branches.IgnoreQueryFilters()
            .CountAsync(branch => branch.TenantId == tenant.TenantId && branch.ExternalId == "branch-revoked"))
            .Should().Be(0, "revoking the company membership must prevent subsequent imports by its existing JWT");
    }

    [PostgreSqlFact]
    public async Task Real_jwt_can_import_locations_only_for_a_company_it_administers()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = await SeedCompanyStockAsync(fixture, suffix);
        var authorizedBranchExternalId = $"location-branch-a-{suffix[..10]}";
        var restrictedBranchExternalId = $"location-branch-b-{suffix[..10]}";
        var authorizedLocationExternalId = $"location-a-{suffix[..10]}";
        var restrictedLocationExternalId = $"location-b-{suffix[..10]}";
        int authorizedBranchId;
        int restrictedBranchId;

        await using (var seed = fixture.CreateContext(tenant.TenantId))
        {
            var authorizedBranch = new Branch
            {
                ExternalId = authorizedBranchExternalId,
                CompanyId = tenant.CompanyAId,
                Code = $"LOC-A-{suffix[..8]}",
                Name = "Authorized import branch"
            };
            var restrictedBranch = new Branch
            {
                ExternalId = restrictedBranchExternalId,
                CompanyId = tenant.CompanyBId,
                Code = $"LOC-B-{suffix[..8]}",
                Name = "Restricted import branch"
            };
            seed.AddRange(authorizedBranch, restrictedBranch);
            await seed.SaveChangesAsync();
            authorizedBranchId = authorizedBranch.Id;
            restrictedBranchId = restrictedBranch.Id;
        }

        var companyAdmin = await CreatePersonaAsync(
            "CompanyAdmin",
            CompanyCapability.View | CompanyCapability.Edit | CompanyCapability.Administer,
            suffix,
            tenant.CompanyAId);

        var authorizedCsv = $"external_id,branch_external_id,name,address\n"
            + $"{authorizedLocationExternalId},{authorizedBranchExternalId},Authorized location,";
        var authorizedResponse = await companyAdmin.Client.PostAsJsonAsync(
            "/api/v1/organization/locations/import",
            new { csv = authorizedCsv, dryRun = false, companyId = tenant.CompanyAId });

        authorizedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var authorizedBody = await authorizedResponse.Content.ReadFromJsonAsync<JsonDocument>())
        {
            authorizedBody!.RootElement.GetProperty("data").GetProperty("created").GetInt32().Should().Be(1);
        }

        var restrictedCsv = $"external_id,branch_external_id,name,address\n"
            + $"{restrictedLocationExternalId},{restrictedBranchExternalId},Restricted location,";
        var restrictedResponse = await companyAdmin.Client.PostAsJsonAsync(
            "/api/v1/organization/locations/import",
            new { csv = restrictedCsv, dryRun = false, companyId = tenant.CompanyBId });

        restrictedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await using var verification = fixture.CreateContext(tenant.TenantId);
        var importedLocation = await verification.Locations.IgnoreQueryFilters()
            .SingleAsync(location => location.TenantId == tenant.TenantId &&
                location.ExternalId == authorizedLocationExternalId);
        importedLocation.BranchId.Should().Be(authorizedBranchId);
        (await verification.Branches.IgnoreQueryFilters().AnyAsync(branch =>
            branch.TenantId == tenant.TenantId && branch.Id == restrictedBranchId &&
            branch.CompanyId == tenant.CompanyBId)).Should().BeTrue(
                "the denied target belongs to a real, persisted branch in another company");
        (await verification.Locations.IgnoreQueryFilters().CountAsync(location =>
            location.TenantId == tenant.TenantId && location.ExternalId == restrictedLocationExternalId))
            .Should().Be(0, "the cross-company denial must happen before any location is imported");
    }

    [PostgreSqlFact]
    public async Task Company_admin_cannot_assign_a_location_to_another_company_branch()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = await SeedCompanyStockAsync(fixture, suffix);
        int originalBranchId;
        int sameCompanyTargetBranchId;
        int restrictedBranchId;
        int locationId;

        await using (var seed = fixture.CreateContext(tenant.TenantId))
        {
            var originalBranch = await seed.Branches.SingleAsync(branch =>
                branch.CompanyId == tenant.CompanyAId);
            var restrictedBranch = await seed.Branches.SingleAsync(branch =>
                branch.CompanyId == tenant.CompanyBId);
            var sameCompanyTargetBranch = new Branch
            {
                CompanyId = tenant.CompanyAId,
                Code = $"AUTH-A2-{suffix[..8]}",
                Name = "Synthetic same-company destination"
            };
            var location = new Location
            {
                ExternalId = $"reassign-{suffix[..10]}",
                BranchId = originalBranch.Id,
                Name = "Synthetic reassignable location"
            };
            seed.AddRange(sameCompanyTargetBranch, location);
            await seed.SaveChangesAsync();
            originalBranchId = originalBranch.Id;
            sameCompanyTargetBranchId = sameCompanyTargetBranch.Id;
            restrictedBranchId = restrictedBranch.Id;
            locationId = location.Id;
        }

        var companyAdmin = await CreatePersonaAsync(
            "CompanyAdmin",
            CompanyCapability.View | CompanyCapability.Edit | CompanyCapability.Administer,
            suffix,
            tenant.CompanyAId);
        using var client = companyAdmin.Client;

        using var allowedResponse = await client.PutAsJsonAsync(
            $"/api/v1/organization/locations/{locationId}/branch",
            new { BranchId = sameCompanyTargetBranchId });
        allowedResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "a company administrator may reassign a synthetic location within its own company");

        using var deniedResponse = await client.PutAsJsonAsync(
            $"/api/v1/organization/locations/{locationId}/branch",
            new { BranchId = restrictedBranchId });
        deniedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a grant for company A must not authorize assigning its location to company B");

        await using var verification = fixture.CreateContext(tenant.TenantId);
        var persistedLocation = await verification.Locations.SingleAsync(location => location.Id == locationId);
        persistedLocation.BranchId.Should().Be(sameCompanyTargetBranchId,
            "the denied cross-company request must preserve the successful same-company assignment");
        (await verification.Branches.SingleAsync(branch => branch.Id == originalBranchId)).CompanyId
            .Should().Be(tenant.CompanyAId);
        (await verification.Branches.SingleAsync(branch => branch.Id == sameCompanyTargetBranchId)).CompanyId
            .Should().Be(tenant.CompanyAId);
        (await verification.Branches.SingleAsync(branch => branch.Id == restrictedBranchId)).CompanyId
            .Should().Be(tenant.CompanyBId);
        (await verification.StockInHand.CountAsync(stock => stock.LocationId == locationId)).Should().Be(0);

        var existingStock = await verification.StockInHand.SingleAsync(stock =>
            stock.ItemId == tenant.ItemId && stock.LocationId == tenant.LocationAId);
        existingStock.Quantity.Should().Be(10);
        existingStock.ReservedQuantity.Should().Be(0);
    }

    [PostgreSqlFact]
    public async Task Real_jwt_item_reads_recheck_active_company_membership_after_revocation()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = await SeedCompanyStockAsync(fixture, suffix);
        var auditor = await CreatePersonaAsync(
            "RestrictedAuditor", CompanyCapability.View, suffix, tenant.CompanyAId);
        var signedJwt = auditor.Client.DefaultRequestHeaders.Authorization!.Parameter;

        (await auditor.Client.GetAsync("/api/v1/items")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await auditor.Client.GetAsync($"/api/v1/items/{tenant.ItemId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var revoke = fixture.CreateContext(tenant.TenantId))
        {
            var membership = await revoke.CompanyMemberships.SingleAsync(candidate =>
                candidate.CompanyId == tenant.CompanyAId && candidate.UserId == auditor.User.Id);
            membership.IsActive.Should().BeTrue();
            membership.IsActive = false;
            await revoke.SaveChangesAsync();
        }

        auditor.Client.DefaultRequestHeaders.Authorization!.Parameter.Should().Be(signedJwt,
            "the authorization check must use the same still-valid JWT after database membership revocation");
        (await auditor.Client.GetAsync("/api/v1/items"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "the item-list query must recheck the current company membership");
        (await auditor.Client.GetAsync($"/api/v1/items/{tenant.ItemId}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "the item-detail query must recheck the current company membership");
    }

    [PostgreSqlFact]
    public async Task Existing_jwt_loses_stock_post_permission_when_company_membership_post_capability_is_removed()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = await SeedCompanyStockAsync(fixture, suffix);
        var poster = await CreatePersonaAsync(
            "Operator", CompanyCapability.View | CompanyCapability.Post, suffix, tenant.CompanyAId);
        var signedJwt = poster.Client.DefaultRequestHeaders.Authorization!.Parameter;

        await using (var initial = fixture.CreateContext(tenant.TenantId))
        {
            (await initial.StockInHand.SingleAsync(stock =>
                stock.ItemId == tenant.ItemId && stock.LocationId == tenant.LocationAId))
                .Quantity.Should().Be(10);
            (await initial.StockTransactions.CountAsync(transaction => transaction.ItemId == tenant.ItemId))
                .Should().Be(0);
        }

        var receipt = new
        {
            ItemId = tenant.ItemId,
            LocationId = tenant.LocationAId,
            Quantity = 2,
            Notes = "authorized post before capability reduction"
        };
        var receiveKey = $"capability-revocation-{Guid.NewGuid():N}";
        var authorizedResponse = await PostStockReceiveAsync(poster.Client, receipt, receiveKey);
        authorizedResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var afterAuthorizedPost = fixture.CreateContext(tenant.TenantId))
        {
            (await afterAuthorizedPost.StockInHand.SingleAsync(stock =>
                stock.ItemId == tenant.ItemId && stock.LocationId == tenant.LocationAId))
                .Quantity.Should().Be(12, "the authorized receive must persist before the grant changes");
            (await afterAuthorizedPost.StockTransactions.CountAsync(transaction => transaction.ItemId == tenant.ItemId))
                .Should().Be(1, "the authorized receive must create exactly one stock transaction");
        }

        await using (var revokePost = fixture.CreateContext(tenant.TenantId))
        {
            var membership = await revokePost.CompanyMemberships.SingleAsync(candidate =>
                candidate.CompanyId == tenant.CompanyAId && candidate.UserId == poster.User.Id);
            membership.IsActive.Should().BeTrue();
            membership.Capabilities.Should().Be(CompanyCapability.View | CompanyCapability.Post);
            membership.Capabilities = CompanyCapability.View;
            await revokePost.SaveChangesAsync();
        }

        poster.Client.DefaultRequestHeaders.Authorization!.Parameter.Should().Be(signedJwt,
            "the retry uses the same already-issued signed JWT");
        var forbiddenRetry = await PostStockReceiveAsync(poster.Client, receipt, receiveKey);
        forbiddenRetry.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the active membership retains View but no longer grants Post");

        await using var final = fixture.CreateContext(tenant.TenantId);
        (await final.StockInHand.SingleAsync(stock =>
            stock.ItemId == tenant.ItemId && stock.LocationId == tenant.LocationAId))
            .Quantity.Should().Be(12, "the forbidden retry must not apply another stock movement");
        (await final.StockTransactions.CountAsync(transaction => transaction.ItemId == tenant.ItemId))
            .Should().Be(1, "the forbidden retry must not persist a second stock transaction");
    }

    [PostgreSqlFact]
    public async Task Real_jwt_persona_matrix_filters_companies_and_enforces_transfer_approval_separation()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = await SeedCompanyStockAsync(fixture, suffix);
        var personas = new Dictionary<string, (ApplicationUser User, HttpClient Client)>(StringComparer.Ordinal)
        {
            ["Operator"] = await CreatePersonaAsync("Operator", CompanyCapability.View | CompanyCapability.Post, suffix, tenant.CompanyAId),
            ["Buyer"] = await CreatePersonaAsync("Buyer", CompanyCapability.View | CompanyCapability.Edit, suffix, tenant.CompanyAId),
            ["Accountant"] = await CreatePersonaAsync("Accountant", CompanyCapability.View | CompanyCapability.Approve |
                CompanyCapability.Post | CompanyCapability.Reverse | CompanyCapability.OverrideExpiredStock, suffix, tenant.CompanyAId),
            ["Cashier"] = await CreatePersonaAsync("Cashier", CompanyCapability.View | CompanyCapability.Post, suffix, tenant.CompanyAId),
            ["CompanyAdmin"] = await CreatePersonaAsync("CompanyAdmin", CompanyCapability.View | CompanyCapability.Edit |
                CompanyCapability.Administer, suffix, tenant.CompanyAId),
            ["RestrictedAuditor"] = await CreatePersonaAsync("RestrictedAuditor", CompanyCapability.View, suffix, tenant.CompanyAId)
        };

        foreach (var (role, persona) in personas)
        {
            var companiesResponse = await persona.Client.GetAsync("/api/v1/organization/companies");
            companiesResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"{role} can view its granted company");
            using var companiesBody = await companiesResponse.Content.ReadFromJsonAsync<JsonDocument>();
            companiesBody!.RootElement.GetProperty("data").EnumerateArray()
                .Select(company => company.GetProperty("id").GetInt32())
                .Should().Equal(tenant.CompanyAId);
            (await persona.Client.GetAsync($"/api/v1/organization/companies/{tenant.CompanyBId}"))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} has no grant for company B");
            (await persona.Client.GetAsync($"/api/v1/stock/in-hand/{tenant.ItemId}/{tenant.LocationBId}"))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} cannot read company B stock");
        }

        var stockResponse = await personas["RestrictedAuditor"].Client.GetAsync("/api/v1/stock/in-hand");
        stockResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var stockBody = await stockResponse.Content.ReadFromJsonAsync<JsonDocument>())
        {
            stockBody!.RootElement.GetProperty("data").EnumerateArray()
                .Select(stock => stock.GetProperty("locationId").GetInt32())
                .Order()
                .Should().Equal(tenant.LocationAId, tenant.LocationA2Id);
        }

        using var createTransferRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfer-orders")
        {
            Content = JsonContent.Create(new
            {
                companyId = tenant.CompanyAId,
                fromLocationId = tenant.LocationAId,
                toLocationId = tenant.LocationA2Id,
                lines = new[] { new { itemId = tenant.ItemId, quantity = 2 } },
                notes = "synthetic persona-matrix approval test"
            })
        };
        createTransferRequest.Headers.Add("Idempotency-Key", $"persona-transfer-{suffix}");
        var createTransfer = await personas["Buyer"].Client.SendAsync(createTransferRequest);
        createTransfer.StatusCode.Should().Be(HttpStatusCode.Created);
        createTransfer.Headers.Location.Should().NotBeNull();
        using var createdTransferBody = await createTransfer.Content.ReadFromJsonAsync<JsonDocument>();
        var transferId = createdTransferBody!.RootElement.GetProperty("data").GetProperty("id").GetInt32();

        (await personas["Buyer"].Client.PostAsync($"/api/v1/transfer-orders/{transferId}/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "Buyer can create but cannot approve");
        (await personas["Accountant"].Client.PostAsync($"/api/v1/transfer-orders/{transferId}/approve", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent, "a separate Accountant principal can approve");

        var crossCompanyReceive = await PostStockReceiveAsync(personas["Operator"].Client, new
        {
            ItemId = tenant.ItemId,
            LocationId = tenant.LocationBId,
            Quantity = 3,
            Notes = "cross-company receive must be rejected"
        });
        crossCompanyReceive.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var crossCompanyTransfer = await personas["Operator"].Client.PostAsJsonAsync("/api/v1/stock/transfer", new
        {
            ItemId = tenant.ItemId,
            FromLocationId = tenant.LocationAId,
            ToLocationId = tenant.LocationBId,
            Quantity = 2,
            Notes = "cross-company transfer must be rejected"
        });
        crossCompanyTransfer.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var restrictedMutation = await PostStockReceiveAsync(personas["RestrictedAuditor"].Client, new
        {
            ItemId = tenant.ItemId,
            LocationId = tenant.LocationAId,
            Quantity = 1,
            Notes = "view-only auditor must not post"
        });
        restrictedMutation.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await PostStockReceiveAsync(personas["Operator"].Client, new
        {
            ItemId = tenant.ItemId,
            LocationId = tenant.LocationAId,
            Quantity = 1,
            Notes = "operator post within granted company"
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await personas["Cashier"].Client.PostAsJsonAsync("/api/v1/stock/sell", new
        {
            ItemId = tenant.ItemId,
            LocationId = tenant.LocationAId,
            Quantity = 1,
            Notes = "cashier sale within granted company"
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await personas["CompanyAdmin"].Client.GetAsync(
            $"/api/v1/organization/companies/{tenant.CompanyAId}/memberships"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await personas["Buyer"].Client.GetAsync(
            $"/api/v1/organization/companies/{tenant.CompanyAId}/memberships"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await using var verify = fixture.CreateContext(tenant.TenantId);
        var stockRows = await verify.StockInHand
            .Where(stock => stock.ItemId == tenant.ItemId)
            .OrderBy(stock => stock.LocationId)
            .ToListAsync();
        stockRows.Select(stock => (stock.LocationId, stock.Quantity)).Should().Equal(
            (tenant.LocationAId, 10),
            (tenant.LocationA2Id, 5),
            (tenant.LocationBId, 20));
        (await verify.TransferOrders.SingleAsync(order => order.Id == transferId)).Status
            .Should().Be(TransferOrderStatus.Approved);
        (await verify.StockInHand.SingleAsync(stock =>
                stock.ItemId == tenant.ItemId && stock.LocationId == tenant.LocationAId))
            .ReservedQuantity.Should().Be(2);
        (await verify.StockTransactions.CountAsync(transaction => transaction.ItemId == tenant.ItemId)).Should().Be(2);
    }

    private async Task<(ApplicationUser User, HttpClient Client)> CreatePersonaAsync(
        string role,
        CompanyCapability capabilities,
        string suffix,
        int companyId)
    {
        var user = await _factory.EnsurePersonaUserAsync(role, suffix);
        await using (var context = fixture.CreateContext("test-tenant"))
        {
            context.CompanyMemberships.Add(new CompanyMembership
            {
                CompanyId = companyId,
                UserId = user.Id,
                Capabilities = capabilities,
                IsActive = true
            });
            await context.SaveChangesAsync();
        }
        return (user, _factory.CreateAuthenticatedClient(user, role));
    }

    private static async Task<SyntheticTenant> SeedCompanyStockAsync(
        PostgreSqlIntegrationFixture fixture,
        string suffix)
    {
        await using var context = fixture.CreateContext("test-tenant");
        var companyA = new Company
        {
            Code = $"AUTH-A-{suffix[..10]}",
            LegalName = "Synthetic authorized company",
            BaseCurrency = "USD"
        };
        var companyB = new Company
        {
            Code = $"AUTH-B-{suffix[..10]}",
            LegalName = "Synthetic restricted company",
            BaseCurrency = "USD"
        };
        var branchA = new Branch { Company = companyA, Code = "AUTH-A", Name = "Authorized branch" };
        var branchB = new Branch { Company = companyB, Code = "AUTH-B", Name = "Restricted branch" };
        var locationA = new Location { Branch = branchA, Name = "Authorized location" };
        var locationA2 = new Location { Branch = branchA, Name = "Authorized destination" };
        var locationB = new Location { Branch = branchB, Name = "Restricted location" };
        var item = new Item
        {
            ItemCode = $"AUTH-ITEM-{suffix[..10]}",
            Description = "Synthetic authorization item",
            Rate = 1m
        };
        context.AddRange(companyA, companyB, branchA, branchB, locationA, locationA2, locationB, item,
            new StockInHand { Item = item, Location = locationA, Quantity = 10 },
            new StockInHand { Item = item, Location = locationA2, Quantity = 5 },
            new StockInHand { Item = item, Location = locationB, Quantity = 20 });
        await context.SaveChangesAsync();

        return new SyntheticTenant("test-tenant", companyA.Id, companyB.Id,
            item.Id, locationA.Id, locationA2.Id, locationB.Id);
    }

    public void Dispose() => _factory.Dispose();

    private sealed record SyntheticTenant(
        string TenantId,
        int CompanyAId,
        int CompanyBId,
        int ItemId,
        int LocationAId,
        int LocationA2Id,
        int LocationBId);
}

internal sealed class PostgreSqlCompanyApiFactory(
    PostgreSqlIntegrationFixture fixture,
    string applicationName = "merconiq-company-api")
    : WebApplicationFactory<Merconiq.Web.Program>
{
    private const string TestTenantId = "test-tenant";
    private const string TestJwtSecret = "testing-only-jwt-secret-with-at-least-32-bytes";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("JwtSettings:Secret", TestJwtSecret);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                ApplicationName = applicationName
            }.ConnectionString;
            services.AddDbContext<InventoryDbContext>(options => options.UseNpgsql(connectionString));
            services.AddScoped<TenantContext>(_ =>
            {
                var context = new TenantContext();
                context.SetTenant(TestTenantId);
                return context;
            });
        });
    }

    public async Task<ApplicationUser> EnsurePersonaUserAsync(string role, string suffix)
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userId = $"postgres-company-capability-{role.ToLowerInvariant()}-{suffix}";
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = userId,
                UserName = $"postgres-company-capability-{role.ToLowerInvariant()}-{suffix}@test-tenant.test",
                Email = $"postgres-company-capability-{role.ToLowerInvariant()}-{suffix}@test-tenant.test",
                EmailConfirmed = true,
                TenantId = TestTenantId
            };
            var create = await userManager.CreateAsync(user);
            if (!create.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", create.Errors.Select(error => error.Code)));
            }
        }

        if (!await roleManager.RoleExistsAsync(role))
        {
            var createRole = await roleManager.CreateAsync(new IdentityRole(role));
            if (!createRole.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", createRole.Errors.Select(error => error.Code)));
            }
        }

        if (!await userManager.IsInRoleAsync(user, role))
        {
            var addRole = await userManager.AddToRoleAsync(user, role);
            if (!addRole.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", addRole.Errors.Select(error => error.Code)));
            }
        }

        return (await userManager.FindByIdAsync(userId))!;
    }

    public HttpClient CreateAuthenticatedClient(ApplicationUser user, string role)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(JwtClaimsFactory.Create(user, TestTenantId, [role])),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Issuer = "Merconiq",
            Audience = "Merconiq",
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret)),
                SecurityAlgorithms.HmacSha256Signature)
        });
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenHandler.WriteToken(token));
        return client;
    }
}
