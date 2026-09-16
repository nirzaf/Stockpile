using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;

namespace Merconiq.Tests.Integration;

public class SwaggerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SwaggerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void V1_openapi_document_describes_the_items_api()
    {
        using var scope = _factory.Services.CreateScope();
        var swaggerProvider = scope.ServiceProvider.GetRequiredService<ISwaggerProvider>();
        var document = swaggerProvider.GetSwagger("v1");

        document.Info.Title.Should().Be("Merconiq API");
        document.Paths.Should().ContainKey("/api/v1/items");
        document.Components.Should().NotBeNull();
        document.Components!.SecuritySchemes.Should().ContainKey("Bearer");
    }
}
