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

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class CompanyCapabilityPostgreSqlApiTests(PostgreSqlIntegrationFixture fixture) : IDisposable
{
    private readonly PostgreSqlCompanyApiFactory _factory = new(fixture);

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

        var crossCompanyReceive = await personas["Operator"].Client.PostAsJsonAsync("/api/v1/stock/receive", new
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

        var restrictedMutation = await personas["RestrictedAuditor"].Client.PostAsJsonAsync("/api/v1/stock/receive", new
        {
            ItemId = tenant.ItemId,
            LocationId = tenant.LocationAId,
            Quantity = 1,
            Notes = "view-only auditor must not post"
        });
        restrictedMutation.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await personas["Operator"].Client.PostAsJsonAsync("/api/v1/stock/receive", new
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

internal sealed class PostgreSqlCompanyApiFactory(PostgreSqlIntegrationFixture fixture)
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
            services.AddDbContext<InventoryDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
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
