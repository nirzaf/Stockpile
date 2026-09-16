using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryManagementSystem.Tests.Integration;

public sealed class LoginAbuseTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public LoginAbuseTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Token_login_uses_identity_lockout_after_repeated_failures()
    {
        var email = $"lockout-{Guid.NewGuid():N}@example.com";
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                TenantId = "test-tenant"
            };
            (await userManager.CreateAsync(user, "Password1")).Succeeded.Should().BeTrue();
        }

        using var client = _factory.CreateUnauthenticatedClient();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/auth/token",
                new { Username = email, Password = "WrongPassword1" });

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            (await userManager.IsLockedOutAsync(user!)).Should().BeTrue();
        }

        var lockedResponse = await client.PostAsJsonAsync(
            "/api/v1/auth/token",
            new { Username = email, Password = "Password1" });
        lockedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
