using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Infrastructure.Data;
using InventoryManagementSystem.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit.Sdk;

namespace InventoryManagementSystem.Tests.Integration;

/// <summary>
/// Shared PostgreSQL lifecycle for the opt-in relational integration suite.
/// </summary>
public sealed class PostgreSqlIntegrationFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public bool IsEnabled => string.Equals(
        Environment.GetEnvironmentVariable("RUN_POSTGRES_TESTS"),
        "true",
        StringComparison.OrdinalIgnoreCase);

    public string ConnectionString
    {
        get
        {
            EnsureEnabled();
            return _container!.GetConnectionString();
        }
    }

    public async Task InitializeAsync()
    {
        if (!IsEnabled)
        {
            return;
        }

        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("stockpile_integration")
            .WithUsername("stockpile")
            .WithPassword("stockpile-test-password")
            .Build();

        await _container.StartAsync();

        await using var context = CreateContext("migration-check");
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public InventoryDbContext CreateContext(string tenantId)
    {
        EnsureEnabled();

        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new InventoryDbContext(options, new TestTenantContext(tenantId));
    }

    public void EnsureEnabled()
    {
        if (!IsEnabled)
        {
            throw SkipException.ForSkip(
                "PostgreSQL integration tests are opt-in. Set RUN_POSTGRES_TESTS=true to run them.");
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlIntegrationCollection : ICollectionFixture<PostgreSqlIntegrationFixture>
{
    public const string Name = "PostgreSQL integration collection";
}
