using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Encodings.Web;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using InventoryManagementSystem.Web.Tenancy;

namespace InventoryManagementSystem.Tests.Integration;

public class CustomWebApplicationFactory : WebApplicationFactory<InventoryManagementSystem.Web.Program>
{
    private const string TestJwtSecret = "testing-only-jwt-secret-with-at-least-32-bytes";
    private readonly string _dbName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("JwtSettings:Secret", TestJwtSecret);

        builder.ConfigureTestServices(services =>
        {
            // Add InMemory DB for testing (PostgreSQL is skipped in Testing environment)
            services.AddDbContext<InventoryDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));

            // Service-provider-only test setup and hosted services do not pass through HTTP
            // middleware, so give those explicit scopes the same tenant as test requests.
            services.AddScoped<TenantContext>(_ =>
            {
                var context = new TenantContext();
                context.SetTenant("test-tenant");
                return context;
            });

            // Add test authentication handler
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        });
    }

    public HttpClient CreateAuthenticatedClient(string role = "Admin", string tenantId = "test-tenant")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "testuser@test.com"),
                new Claim(ClaimTypes.NameIdentifier, "1"),
                new Claim(ClaimTypes.Role, role),
                new Claim("tenant_id", tenantId)
            }),
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
        client.DefaultRequestHeaders.Add("X-Test-Role", role);
        return client;
    }

    public HttpClient CreateUnauthenticatedClient()
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }
}

public sealed class CookieAuthenticationWebApplicationFactory : CustomWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            });
            services.Configure<SecurityStampValidatorOptions>(options =>
                options.ValidationInterval = TimeSpan.Zero);
        });
    }

    public async Task<HttpClient> CreateCookieClientAsync(ApplicationUser user, string tenantId = "test-tenant")
    {
        using var scope = Services.CreateScope();
        var principalFactory = scope.ServiceProvider
            .GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var principal = await principalFactory.CreateAsync(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        var existingTenantClaim = identity.FindFirst("tenant_id");
        if (existingTenantClaim is not null)
        {
            identity.RemoveClaim(existingTenantClaim);
        }

        identity.AddClaim(new Claim("tenant_id", tenantId));

        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider
        };
        httpContext.Request.Host = new HostString("localhost");
        var authentication = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        await authentication.SignInAsync(
            httpContext,
            IdentityConstants.ApplicationScheme,
            principal,
            new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
            });

        var setCookie = httpContext.Response.Headers["Set-Cookie"].Single();
        var cookie = setCookie[..setCookie.IndexOf(';')];
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
        return client;
    }
}

public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey("X-Test-Auth"))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var role = Request.Headers["X-Test-Role"].FirstOrDefault() ?? "Admin";

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "testuser@test.com"),
            new Claim(ClaimTypes.Email, "testuser@test.com"),
            new Claim(ClaimTypes.Role, role),
            new Claim(ClaimTypes.NameIdentifier, "1")
        };

        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "Test");

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
