using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

internal sealed class PostgreSqlItemApiFactory(PostgreSqlIntegrationFixture fixture) : CustomWebApplicationFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<InventoryDbContext>();
            services.AddDbContext<InventoryDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        });
    }
}
