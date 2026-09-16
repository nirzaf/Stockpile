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
public sealed class DuplicateItemPostgreSqlApiTests : IDisposable
{
    private readonly PostgreSqlItemApiFactory _factory;
    private readonly PostgreSqlIntegrationFixture _fixture;

    public DuplicateItemPostgreSqlApiTests(PostgreSqlIntegrationFixture fixture)
    {
        _fixture = fixture;
        _factory = new PostgreSqlItemApiFactory(fixture);
    }

    [PostgreSqlFact]
    public async Task Same_tenant_duplicate_item_code_returns_conflict_and_keeps_one_row()
    {
        _fixture.EnsureEnabled();
        using var client = _factory.CreateAuthenticatedClient();
        var itemCode = $"DUP-{Guid.NewGuid():N}"[..20];
        var command = new { ItemCode = itemCode, Description = "first", Rate = 10m };

        var first = await client.PostAsJsonAsync("/api/v1/items", command);
        var duplicate = await client.PostAsJsonAsync("/api/v1/items", new
        {
            ItemCode = itemCode,
            Description = "duplicate",
            Rate = 20m
        });

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await using var verify = _fixture.CreateContext("test-tenant");
        (await verify.Items.CountAsync(item => item.ItemCode == itemCode)).Should().Be(1);
    }

    public void Dispose() => _factory.Dispose();
}

internal sealed class PostgreSqlItemApiFactory(PostgreSqlIntegrationFixture fixture) : WebApplicationFactory<Merconiq.Web.Program>
{
    private const string TestUserId = "postgres-duplicate-item-admin";
    private const string TestTenantId = "test-tenant";
    private const string TestJwtSecret = "testing-only-jwt-secret-with-at-least-32-bytes";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("JwtSettings:Secret", TestJwtSecret);
        builder.ConfigureTestServices(services =>
        {
            // This factory exercises the item API only. Do not let unrelated hosted workers
            // claim shared PostgreSQL deliveries and stop the TestServer during the test.
            services.RemoveAll<IHostedService>();

            services.AddDbContext<InventoryDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
            services.AddScoped<TenantContext>(_ =>
            {
                var context = new TenantContext();
                context.SetTenant("test-tenant");
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
                UserName = "postgres-duplicate-item-admin@test-tenant.test",
                Email = "postgres-duplicate-item-admin@test-tenant.test",
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
