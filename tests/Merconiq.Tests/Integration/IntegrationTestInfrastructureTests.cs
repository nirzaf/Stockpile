using FluentAssertions;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

public class IntegrationTestInfrastructureTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public IntegrationTestInfrastructureTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void TestingHost_UsesInMemoryDatabaseProvider()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        db.Database.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.InMemory");
    }
}
