using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Infrastructure.Data;
using InventoryManagementSystem.Web.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace InventoryManagementSystem.Tests.Integration;

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

internal sealed class PostgreSqlItemApiFactory(PostgreSqlIntegrationFixture fixture) : WebApplicationFactory<InventoryManagementSystem.Web.Program>
{
    private const string TestJwtSecret = "testing-only-jwt-secret-with-at-least-32-bytes";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("JwtSettings:Secret", TestJwtSecret);
        builder.ConfigureTestServices(services =>
        {
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
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("tenant_id", "test-tenant")]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            Issuer = "InventoryManagementSystem",
            Audience = "InventoryManagementSystem",
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret)),
                SecurityAlgorithms.HmacSha256Signature)
        });
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenHandler.WriteToken(token));
        client.DefaultRequestHeaders.Add("X-Test-Auth", "true");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Admin");
        return client;
    }
}
