using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

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
    public async Task Stock_mutation_requires_an_inventory_role()
    {
        using var client = _factory.CreateAuthenticatedClient(role: "Viewer");

        var response = await client.PostAsJsonAsync(
            "/api/v1/stock/receive",
            new { ItemId = 1, LocationId = 1, Quantity = 1, Notes = "role check" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
