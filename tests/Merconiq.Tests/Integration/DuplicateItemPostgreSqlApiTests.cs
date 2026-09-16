using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private const string TestSecurityStamp = "postgres-duplicate-item-security-stamp";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
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
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, PostgreSqlItemApiAuthHandler>("Test", _ => { });
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        EnsureAdminUser();
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Auth", "true");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Admin");
        client.DefaultRequestHeaders.Add("X-Test-UserId", TestUserId);
        client.DefaultRequestHeaders.Add("X-Test-Tenant", TestTenantId);
        client.DefaultRequestHeaders.Add("X-Test-SecurityStamp", TestSecurityStamp);
        return client;
    }

    private void EnsureAdminUser()
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
                TenantId = TestTenantId,
                SecurityStamp = TestSecurityStamp
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
    }
}

internal sealed class PostgreSqlItemApiAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("X-Test-Auth"))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "postgres-duplicate-item-admin@test-tenant.test"),
            new Claim(ClaimTypes.Email, "postgres-duplicate-item-admin@test-tenant.test"),
            new Claim(ClaimTypes.Role, Request.Headers["X-Test-Role"].FirstOrDefault() ?? "Admin"),
            new Claim(ClaimTypes.NameIdentifier, Request.Headers["X-Test-UserId"].FirstOrDefault() ?? string.Empty),
            new Claim("tenant_id", Request.Headers["X-Test-Tenant"].FirstOrDefault() ?? string.Empty),
            new Claim("AspNet.Identity.SecurityStamp", Request.Headers["X-Test-SecurityStamp"].FirstOrDefault() ?? string.Empty)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
    }
}
