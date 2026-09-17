using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Tests.Infrastructure;
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
public sealed class TenantBoundaryPostgreSqlApiTests(PostgreSqlIntegrationFixture fixture) : IDisposable
{
    internal const string TenantAId = "cross-tenant-a";
    internal const string TenantBId = "cross-tenant-b";
    internal const string TenantAHost = "tenant-a.test";
    internal const string TenantBHost = "tenant-b.test";
    private readonly CrossTenantApiFactory _factory = new(fixture);

    [PostgreSqlFact]
    public async Task Tenant_a_jwt_cannot_read_or_mutate_tenant_b_company_or_stock_data()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var tenantA = await SeedTenantAsync(fixture, TenantAId, "A", suffix, 11);
        var tenantB = await SeedTenantAsync(fixture, TenantBId, "B", suffix, 23);
        var companyAdminCapabilities = CompanyCapability.View | CompanyCapability.Edit |
                                      CompanyCapability.Administer | CompanyCapability.Post;
        var userA = await _factory.EnsurePersonaUserAsync("CompanyAdmin", TenantAId, suffix);
        var operatorA = await _factory.EnsurePersonaUserAsync("Operator", TenantAId, suffix);
        var userB = await _factory.EnsurePersonaUserAsync("CompanyAdmin", TenantBId, suffix);
        await GrantCompanyCapabilitiesAsync(fixture, TenantAId, tenantA.CompanyId, userA.Id,
            companyAdminCapabilities);
        await GrantCompanyCapabilitiesAsync(fixture, TenantAId, tenantA.CompanyId, operatorA.Id,
            CompanyCapability.View | CompanyCapability.Post);
        await GrantCompanyCapabilitiesAsync(fixture, TenantBId, tenantB.CompanyId, userB.Id,
            companyAdminCapabilities);

        using var tenantAClient = _factory.CreateAuthenticatedClient(userA, "CompanyAdmin", TenantAId, TenantAHost);
        using var tenantAOperatorClient = _factory.CreateAuthenticatedClient(
            operatorA, "Operator", TenantAId, TenantAHost);
        using var tenantBClient = _factory.CreateAuthenticatedClient(userB, "CompanyAdmin", TenantBId, TenantBHost);
        using var tenantAAdminOnTenantBHost = _factory.CreateAuthenticatedClient(
            userA, "CompanyAdmin", TenantAId, TenantBHost);
        using var tenantAOperatorOnTenantBHost = _factory.CreateAuthenticatedClient(
            operatorA, "Operator", TenantAId, TenantBHost);

        using var tenantACompanies = await tenantAClient.GetAsync("/api/v1/organization/companies");
        tenantACompanies.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var body = await tenantACompanies.Content.ReadFromJsonAsync<JsonDocument>())
        {
            body!.RootElement.GetProperty("data").EnumerateArray()
                .Select(company => company.GetProperty("id").GetInt32())
                .Should().Equal(tenantA.CompanyId);
        }

        using var tenantAStock = await tenantAClient.GetAsync("/api/v1/stock/in-hand");
        tenantAStock.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var body = await tenantAStock.Content.ReadFromJsonAsync<JsonDocument>())
        {
            body!.RootElement.GetProperty("data").EnumerateArray()
                .Select(stock => stock.GetProperty("locationId").GetInt32())
                .Should().Equal(tenantA.LocationId);
        }

        using var tenantAOperatorCompany = await tenantAOperatorClient.GetAsync(
            $"/api/v1/organization/companies/{tenantA.CompanyId}");
        tenantAOperatorCompany.StatusCode.Should().Be(HttpStatusCode.OK);

        using var tenantBCompany = await tenantBClient.GetAsync(
            $"/api/v1/organization/companies/{tenantB.CompanyId}");
        tenantBCompany.StatusCode.Should().Be(HttpStatusCode.OK,
            "the separately-scoped tenant B host and principal can access tenant B data");
        using var tenantBStock = await tenantBClient.GetAsync(
            $"/api/v1/stock/in-hand/{tenantB.ItemId}/{tenantB.LocationId}");
        tenantBStock.StatusCode.Should().Be(HttpStatusCode.OK);

        var companyUpdateRequest = new UpdateCompanyRequest(
            LegalName: "Authorized tenant B company update",
            TradingName: null,
            RegistrationNumber: null,
            TaxIdentifier: null,
            BaseCurrency: "USD",
            CountryCode: "US",
            IsActive: true);
        using var authorizedCompanyUpdate = await tenantBClient.PutAsJsonAsync(
            $"/api/v1/organization/companies/{tenantB.CompanyId}", companyUpdateRequest);
        authorizedCompanyUpdate.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "the same valid company-update request succeeds for a tenant B CompanyAdmin");

        using var crossTenantCompanyRead = await tenantAAdminOnTenantBHost.GetAsync(
            $"/api/v1/organization/companies/{tenantB.CompanyId}");
        crossTenantCompanyRead.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the signed tenant A token must not authenticate on the tenant B host");
        using var crossTenantStockRead = await tenantAAdminOnTenantBHost.GetAsync(
            $"/api/v1/stock/in-hand/{tenantB.ItemId}/{tenantB.LocationId}");
        crossTenantStockRead.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var crossTenantCompanyUpdate = await tenantAAdminOnTenantBHost.PutAsJsonAsync(
            $"/api/v1/organization/companies/{tenantB.CompanyId}", companyUpdateRequest);
        crossTenantCompanyUpdate.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var crossTenantStockReceive = await tenantAOperatorOnTenantBHost.PostAsJsonAsync(
            "/api/v1/stock/receive", new
            {
                ItemId = tenantB.ItemId,
                LocationId = tenantB.LocationId,
                Quantity = 7,
                Notes = "unauthorized cross-tenant receive"
            });
        crossTenantStockReceive.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await using (var verifyA = fixture.CreateContext(TenantAId))
        {
            (await verifyA.Companies.SingleAsync(company => company.Id == tenantA.CompanyId)).LegalName
                .Should().Be(tenantA.LegalName);
            (await verifyA.StockInHand.SingleAsync(stock => stock.ItemId == tenantA.ItemId)).Quantity
                .Should().Be(tenantA.OpeningQuantity);
            (await verifyA.StockTransactions.CountAsync(transaction => transaction.ItemId == tenantA.ItemId))
                .Should().Be(0);
        }

        await using (var verifyB = fixture.CreateContext(TenantBId))
        {
            (await verifyB.Companies.SingleAsync(company => company.Id == tenantB.CompanyId)).LegalName
                .Should().Be(companyUpdateRequest.LegalName,
                    "only the authorized tenant B control update should be persisted");
            (await verifyB.StockInHand.SingleAsync(stock => stock.ItemId == tenantB.ItemId)).Quantity
                .Should().Be(tenantB.OpeningQuantity);
            (await verifyB.StockTransactions.CountAsync(transaction => transaction.ItemId == tenantB.ItemId))
                .Should().Be(0);
        }
    }

    private static async Task<SyntheticTenant> SeedTenantAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenantId,
        string label,
        string suffix,
        int openingQuantity)
    {
        await using var context = fixture.CreateContext(tenantId);
        var legalName = $"Synthetic tenant {label} company {suffix[..10]}";
        var company = new Company
        {
            Code = $"CROSS-{label}-{suffix[..10]}",
            LegalName = legalName,
            BaseCurrency = "USD"
        };
        var branch = new Branch
        {
            Company = company,
            Code = $"CROSS-{label}",
            Name = $"Tenant {label} branch"
        };
        var location = new Location { Branch = branch, Name = $"Tenant {label} location" };
        var item = new Item
        {
            ItemCode = $"CROSS-{label}-{suffix[..10]}",
            Description = $"Synthetic tenant {label} item",
            Rate = 1m
        };
        context.AddRange(company, branch, location, item,
            new StockInHand { Item = item, Location = location, Quantity = openingQuantity });
        await context.SaveChangesAsync();

        return new SyntheticTenant(company.Id, legalName, item.Id, location.Id, openingQuantity);
    }

    private static async Task GrantCompanyCapabilitiesAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenantId,
        int companyId,
        string userId,
        CompanyCapability capabilities)
    {
        await using var context = fixture.CreateContext(tenantId);
        context.CompanyMemberships.Add(new CompanyMembership
        {
            CompanyId = companyId,
            UserId = userId,
            Capabilities = capabilities,
            IsActive = true
        });
        await context.SaveChangesAsync();
    }

    public void Dispose() => _factory.Dispose();

    private sealed record SyntheticTenant(
        int CompanyId,
        string LegalName,
        int ItemId,
        int LocationId,
        int OpeningQuantity);
}

internal sealed class CrossTenantApiFactory(PostgreSqlIntegrationFixture fixture)
    : WebApplicationFactory<Merconiq.Web.Program>
{
    private const string TestJwtSecret = "testing-only-cross-tenant-jwt-secret-32-bytes";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("JwtSettings:Secret", TestJwtSecret);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.AddDbContext<InventoryDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
            services.Configure<TenantOptions>(options =>
            {
                options.HostTenants.Clear();
                options.HostTenants[TenantBoundaryPostgreSqlApiTests.TenantAHost] =
                    TenantBoundaryPostgreSqlApiTests.TenantAId;
                options.HostTenants[TenantBoundaryPostgreSqlApiTests.TenantBHost] =
                    TenantBoundaryPostgreSqlApiTests.TenantBId;
            });
        });
    }

    public async Task<ApplicationUser> EnsurePersonaUserAsync(string role, string tenantId, string suffix)
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId);
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userId = $"cross-tenant-{tenantId}-{role.ToLowerInvariant()}-{suffix}";
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = userId,
                UserName = $"{userId}@test.invalid",
                Email = $"{userId}@test.invalid",
                EmailConfirmed = true,
                TenantId = tenantId
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

    public HttpClient CreateAuthenticatedClient(
        ApplicationUser user,
        string role,
        string tenantId,
        string host)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri($"https://{host}")
        });
        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new System.Security.Claims.ClaimsIdentity(JwtClaimsFactory.Create(user, tenantId, [role])),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Issuer = "Merconiq",
            Audience = "Merconiq",
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret)),
                SecurityAlgorithms.HmacSha256Signature)
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", tokenHandler.WriteToken(token));
        return client;
    }
}
