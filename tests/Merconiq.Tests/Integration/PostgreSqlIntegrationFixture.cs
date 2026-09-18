using Merconiq.Core.Interfaces;
using Merconiq.Infrastructure.Data;
using Merconiq.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Sdk;

namespace Merconiq.Tests.Integration;

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
            // Tests create distinct Npgsql pools when they vary ApplicationName or SearchPath.
            // The pools share one disposable PostgreSQL server and otherwise retain idle
            // connectors for Npgsql's five-minute default, eventually exhausting its
            // default max_connections during the serial integration suite.
            return new NpgsqlConnectionStringBuilder(_container!.GetConnectionString())
            {
                ConnectionIdleLifetime = 5,
                ConnectionPruningInterval = 1
            }.ConnectionString;
        }
    }

    public async Task InitializeAsync()
    {
        if (!IsEnabled)
        {
            return;
        }

        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("merconiq_integration")
            .WithUsername("merconiq")
            .WithPassword("merconiq-test-password")
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

    public InventoryDbContext CreateContext(
        string tenantId,
        string? applicationName = null,
        params IInterceptor[] interceptors)
    {
        EnsureEnabled();

        var connectionString = applicationName is null
            ? ConnectionString
            : new NpgsqlConnectionStringBuilder(ConnectionString) { ApplicationName = applicationName }.ConnectionString;
        var optionsBuilder = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        if (interceptors.Length > 0)
        {
            optionsBuilder.AddInterceptors(interceptors);
        }

        var options = optionsBuilder.Options;

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

/// <summary>Runs only when the PostgreSQL integration suite has been explicitly enabled.</summary>
public sealed class PostgreSqlFactAttribute : Xunit.FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("RUN_POSTGRES_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip = "PostgreSQL integration tests are opt-in. Set RUN_POSTGRES_TESTS=true to run them.";
        }
    }
}
