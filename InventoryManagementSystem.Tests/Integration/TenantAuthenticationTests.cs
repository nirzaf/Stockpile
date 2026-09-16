using System.Net;
using System.Security.Claims;
using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryManagementSystem.Tests.Integration;

public class TenantAuthenticationTests : IClassFixture<CookieAuthenticationWebApplicationFactory>
{
    private readonly CookieAuthenticationWebApplicationFactory _factory;

    public TenantAuthenticationTests(CookieAuthenticationWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Jwt_for_the_resolved_tenant_is_accepted()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/v1/items");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Jwt_for_a_different_tenant_is_rejected()
    {
        using var client = _factory.CreateAuthenticatedClient(tenantId: "tenant-b");

        var response = await client.GetAsync("/api/v1/items");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cookie_principal_contains_the_resolved_tenant_claim()
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var principalFactory = scope.ServiceProvider
            .GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var email = $"cookie-{Guid.NewGuid():N}@example.com";
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            TenantId = "test-tenant"
        };

        var createResult = await userManager.CreateAsync(user, "Password1");
        createResult.Succeeded.Should().BeTrue();

        var principal = await principalFactory.CreateAsync(user);

        principal.FindFirstValue("tenant_id").Should().Be("test-tenant");
        principal.Claims.Where(claim => claim.Type == "tenant_id").Should().ContainSingle();
    }

    [Fact]
    public async Task Cookie_with_changed_security_stamp_is_rejected_by_the_actual_pipeline()
    {
        var user = await CreateUserAsync();
        using var client = await _factory.CreateCookieClientAsync(user);

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var storedUser = await userManager.FindByIdAsync(user.Id);
            storedUser.Should().NotBeNull();
            (await userManager.UpdateSecurityStampAsync(storedUser!)).Succeeded.Should().BeTrue();
        }

        var response = await client.GetAsync("/Account/AccessDenied");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().StartWith("/Account/Login");
    }

    [Fact]
    public async Task Cookie_for_a_different_tenant_is_rejected_by_the_actual_pipeline()
    {
        var user = await CreateUserAsync();
        using var client = await _factory.CreateCookieClientAsync(user, tenantId: "tenant-b");

        var response = await client.GetAsync("/Account/AccessDenied");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().StartWith("/Account/Login");
    }

    [Fact]
    public async Task Valid_same_tenant_cookie_is_accepted_by_the_actual_pipeline()
    {
        var user = await CreateUserAsync();
        using var client = await _factory.CreateCookieClientAsync(user);

        var response = await client.GetAsync("/Account/AccessDenied");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<ApplicationUser> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"cookie-pipeline-{Guid.NewGuid():N}@example.com";
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            TenantId = "test-tenant"
        };

        var createResult = await userManager.CreateAsync(user, "Password1");
        createResult.Succeeded.Should().BeTrue();
        return user;
    }
}
