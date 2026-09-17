using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
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
public sealed class OrganizationMappingPostgreSqlApiTests(PostgreSqlIntegrationFixture fixture) : IDisposable
{
    private readonly PostgreSqlOrganizationApiFactory _factory = new(fixture);

    [PostgreSqlFact]
    public async Task Http_and_persistence_boundaries_reject_cross_tenant_branch_and_preserve_stock()
    {
        fixture.EnsureEnabled();
        var suffix = Guid.NewGuid().ToString("N");
        var tenantA = await SeedExistingStockAsync(fixture, "test-tenant", $"A-{suffix[..10]}");
        var tenantB = await SeedExistingStockAsync(fixture, $"mapping-b-{suffix}", $"B-{suffix[..10]}");

        using var client = _factory.CreateAuthenticatedClient();
        var response = await client.PutAsJsonAsync(
            $"/api/v1/organization/locations/{tenantA.LocationId}/branch",
            new { BranchId = tenantB.BranchId });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await using (var persistence = fixture.CreateContext(tenantA.TenantId))
        {
            var location = await persistence.Locations.SingleAsync(row => row.Id == tenantA.LocationId);
            location.BranchId = tenantB.BranchId;

            var act = async () => await persistence.SaveChangesAsync();
            await act.Should().ThrowAsync<DbUpdateException>();
        }

        await using var verify = fixture.CreateContext(tenantA.TenantId);
        var preservedLocation = await verify.Locations.SingleAsync(row => row.Id == tenantA.LocationId);
        var preservedStock = await verify.StockInHand.SingleAsync(row => row.Id == tenantA.StockId);
        preservedLocation.BranchId.Should().BeNull();
        preservedLocation.TenantId.Should().Be(tenantA.TenantId);
        preservedStock.Id.Should().Be(tenantA.StockId);
        preservedStock.ItemId.Should().Be(tenantA.ItemId);
        preservedStock.LocationId.Should().Be(tenantA.LocationId);
        preservedStock.Quantity.Should().Be(37);
        preservedStock.ReservedQuantity.Should().Be(4);
    }

    public void Dispose() => _factory.Dispose();

    private static async Task<SyntheticLocationMapping> SeedExistingStockAsync(
        PostgreSqlIntegrationFixture fixture,
        string tenantId,
        string code)
    {
        await using var context = fixture.CreateContext(tenantId);
        var company = new Company
        {
            Code = $"CO-{code}",
            LegalName = $"Synthetic company {code}",
            BaseCurrency = "USD"
        };
        var branch = new Branch
        {
            Company = company,
            Code = $"BR-{code}",
            Name = $"Synthetic branch {code}"
        };
        var location = new Location { Name = $"Existing unassigned location {code}" };
        var item = new Item
        {
            ItemCode = $"ITEM-{code}",
            Description = $"Synthetic item {code}",
            Rate = 3.25m
        };
        var stock = new StockInHand
        {
            Item = item,
            Location = location,
            Quantity = 37,
            ReservedQuantity = 4
        };

        context.AddRange(company, branch, location, item, stock);
        await context.SaveChangesAsync();

        return new SyntheticLocationMapping(tenantId, branch.Id, location.Id, item.Id, stock.Id);
    }

    private sealed record SyntheticLocationMapping(
        string TenantId,
        int BranchId,
        int LocationId,
        int ItemId,
        int StockId);
}

internal sealed class PostgreSqlOrganizationApiFactory(PostgreSqlIntegrationFixture fixture)
    : WebApplicationFactory<Merconiq.Web.Program>
{
    private const string TestTenantId = "test-tenant";
    private const string TestUserId = "postgres-organization-mapping-admin";
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

    public HttpClient CreateAuthenticatedClient()
    {
        var user = EnsureAdminUser();
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(JwtClaimsFactory.Create(user, TestTenantId, ["Admin"])),
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

    private ApplicationUser EnsureAdminUser()
    {
        using var scope = Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var user = userManager.FindByIdAsync(TestUserId).GetAwaiter().GetResult();
        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = TestUserId,
                UserName = "postgres-organization-mapping-admin@test-tenant.test",
                Email = "postgres-organization-mapping-admin@test-tenant.test",
                EmailConfirmed = true,
                TenantId = TestTenantId
            };
            var create = userManager.CreateAsync(user).GetAwaiter().GetResult();
            if (!create.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", create.Errors.Select(error => error.Code)));
            }
        }

        if (!roleManager.RoleExistsAsync("Admin").GetAwaiter().GetResult())
        {
            var createRole = roleManager.CreateAsync(new IdentityRole("Admin")).GetAwaiter().GetResult();
            if (!createRole.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", createRole.Errors.Select(error => error.Code)));
            }
        }

        if (!userManager.IsInRoleAsync(user, "Admin").GetAwaiter().GetResult())
        {
            var addRole = userManager.AddToRoleAsync(user, "Admin").GetAwaiter().GetResult();
            if (!addRole.Succeeded)
            {
                throw new InvalidOperationException(string.Join("; ", addRole.Errors.Select(error => error.Code)));
            }
        }

        return userManager.FindByIdAsync(TestUserId).GetAwaiter().GetResult()!;
    }
}
