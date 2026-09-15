using System.Net;
using FluentAssertions;

namespace InventoryManagementSystem.Tests.Integration;

public class CorsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public CorsTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Preflight_allows_configured_origin()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/items");
        request.Headers.Add("Origin", "https://trusted.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        using var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin")
            .Single()
            .Should()
            .Be("https://trusted.example");
    }

    [Fact]
    public async Task Preflight_rejects_unconfigured_origin()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/items");
        request.Headers.Add("Origin", "https://untrusted.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        using var response = await _client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }
}
