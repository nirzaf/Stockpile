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
    public async Task Real_jwt_company_grants_filter_queries_and_block_cross_company_mutations()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = await SeedCompanyStockAsync(fixture, suffix);
        var user = await _factory.EnsureOperatorUserAsync();
        await GrantCompanyAccessAsync(user.Id, tenant.CompanyAId);

        using var client = _factory.CreateAuthenticatedClient(user);

        var companiesResponse = await client.GetAsync("/api/v1/organization/companies");
        companiesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var companiesBody = await companiesResponse.Content.ReadFromJsonAsync<JsonDocument>();
        companiesBody!.RootElement.GetProperty("data").EnumerateArray()
            .Select(company => company.GetProperty("id").GetInt32())
            .Should().Equal(tenant.CompanyAId);

        (await client.GetAsync($"/api/v1/organization/companies/{tenant.CompanyBId}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var stockResponse = await client.GetAsync("/api/v1/stock/in-hand");
        stockResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var stockBody = await stockResponse.Content.ReadFromJsonAsync<JsonDocument>();
        stockBody!.RootElement.GetProperty("data").EnumerateArray()
            .Select(stock => stock.GetProperty("locationId").GetInt32())
            .Should().Equal(tenant.LocationAId);

        (await client.GetAsync($"/api/v1/stock/in-hand/{tenant.ItemId}/{tenant.LocationBId}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var receive = await client.PostAsJsonAsync("/api/v1/stock/receive", new
        {
            ItemId = tenant.ItemId,
            LocationId = tenant.LocationBId,
            Quantity = 3,
            Notes = "cross-company receive must be rejected"
        });
        receive.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var transfer = await client.PostAsJsonAsync("/api/v1/stock/transfer", new
        {
            ItemId = tenant.ItemId,
            FromLocationId = tenant.LocationAId,
            ToLocationId = tenant.LocationBId,
            Quantity = 2,
            Notes = "cross-company transfer must be rejected"
        });
        transfer.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await using var verify = fixture.CreateContext(tenant.TenantId);
        var stockRows = await verify.StockInHand
            .Where(stock => stock.ItemId == tenant.ItemId)
            .OrderBy(stock => stock.LocationId)
            .ToListAsync();
        stockRows.Select(stock => (stock.LocationId, stock.Quantity)).Should().Equal(
            (tenant.LocationAId, 10),
            (tenant.LocationBId, 20));
        (await verify.StockTransactions.CountAsync(transaction => transaction.ItemId == tenant.ItemId))
            .Should().Be(0);
    }

    private async Task GrantCompanyAccessAsync(string userId, int companyId)
    {
        await using var context = fixture.CreateContext("test-tenant");
        context.CompanyMemberships.Add(new CompanyMembership
        {
            CompanyId = companyId,
            UserId = userId,
            Capabilities = CompanyCapability.View | CompanyCapability.Post,
            IsActive = true
        });
        await context.SaveChangesAsync();
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
        var locationB = new Location { Branch = branchB, Name = "Restricted location" };
        var item = new Item
        {
            ItemCode = $"AUTH-ITEM-{suffix[..10]}",
            Description = "Synthetic authorization item",
            Rate = 1m
        };
        context.AddRange(companyA, companyB, branchA, branchB, locationA, locationB, item,
            new StockInHand { Item = item, Location = locationA, Quantity = 10 },
            new StockInHand { Item = item, Location = locationB, Quantity = 20 });
        await context.SaveChangesAsync();

        return new SyntheticTenant("test-tenant", companyA.Id, companyB.Id,
            item.Id, locationA.Id, locationB.Id);
    }

    public void Dispose() => _factory.Dispose();

    private sealed record SyntheticTenant(
        string TenantId,
        int CompanyAId,
        int CompanyBId,
        int ItemId,
        int LocationAId,
        int LocationBId);
}

internal sealed class PostgreSqlCompanyApiFactory(PostgreSqlIntegrationFixture fixture)
    : WebApplicationFactory<Merconiq.Web.Program>
{
    private const string TestTenantId = "test-tenant";
    private const string TestUserId = "postgres-company-capability-operator";
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

    public async Task<ApplicationUser> EnsureOperatorUserAsync()
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var user = await userManager.FindByIdAsync(TestUserId);
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = TestUserId,
                UserName = "postgres-company-capability-operator@test-tenant.test",
                Email = "postgres-company-capability-operator@test-tenant.test",
                EmailConfirmed = true,
                TenantId = TestTenantId
            };
            var create = await userManager.CreateAsync(user);
            if (!create.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", create.Errors.Select(error => error.Code)));
            }
        }

        if (!await roleManager.RoleExistsAsync("Operator"))
        {
            var createRole = await roleManager.CreateAsync(new IdentityRole("Operator"));
            if (!createRole.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", createRole.Errors.Select(error => error.Code)));
            }
        }

        if (!await userManager.IsInRoleAsync(user, "Operator"))
        {
            var addRole = await userManager.AddToRoleAsync(user, "Operator");
            if (!addRole.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", addRole.Errors.Select(error => error.Code)));
            }
        }

        return (await userManager.FindByIdAsync(TestUserId))!;
    }

    public HttpClient CreateAuthenticatedClient(ApplicationUser user)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(JwtClaimsFactory.Create(user, TestTenantId, ["Operator"])),
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
