using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

public sealed class ApiContractTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ApiContractTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Unmapped_host_is_rejected_before_authentication()
    {
        using var client = _factory.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/items");
        request.Headers.Host = "unmapped.example";

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Stock_mutation_requires_post_capability()
    {
        using var client = _factory.CreateAuthenticatedClient(role: "Viewer");

        var response = await client.PostAsJsonAsync(
            "/api/v1/stock/receive",
            new { ItemId = 1, LocationId = 1, Quantity = 1, Notes = "role check" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Stock_read_is_available_to_viewers()
    {
        using var client = _factory.CreateAuthenticatedClient(role: "Viewer");

        (await client.GetAsync("/api/v1/stock/in-hand")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var viewer = (await users.FindByNameAsync("viewer@test-tenant.test"))!;
            var company = new Company
            {
                Code = $"VIEW-{Guid.NewGuid():N}"[..12],
                LegalName = "Viewer company"
            };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            db.CompanyMemberships.Add(new CompanyMembership
            {
                CompanyId = company.Id,
                UserId = viewer.Id,
                Capabilities = CompanyCapability.View,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/v1/stock/in-hand");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Webhook_configuration_is_restricted_to_tenant_administrators()
    {
        using var client = _factory.CreateAuthenticatedClient(role: "Viewer");

        var read = await client.GetAsync("/api/v1/webhooks");
        var edit = await client.PostAsJsonAsync("/api/v1/webhooks", new
        {
            Url = "https://example.com/hooks",
            EventType = "StockChanged",
            IsActive = true
        });

        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        edit.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var adminClient = _factory.CreateAuthenticatedClient(role: "Admin");
        (await adminClient.GetAsync("/api/v1/webhooks")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
