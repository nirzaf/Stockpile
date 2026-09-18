using System.Collections.Concurrent;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Options;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.BackgroundServices;
using Merconiq.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class ForecastBackgroundServicePostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Hosted_forecast_cycle_queries_only_each_configured_tenants_data()
    {
        fixture.EnsureEnabled();

        var suffix = Guid.NewGuid().ToString("N");
        var tenantAId = $"hosted-forecast-a-{suffix}";
        var tenantBId = $"hosted-forecast-b-{suffix}";
        var tenantAItemId = await SeedItemAsync(tenantAId, $"HFA-{suffix}");
        var tenantBItemId = await SeedItemAsync(tenantBId, $"HFB-{suffix}");

        var observations = new ConcurrentQueue<ForecastQueryObservation>();
        var bothTenantsObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<TenantContext>());
        services.AddDbContext<InventoryDbContext>(options =>
            options.UseNpgsql(fixture.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddSingleton(observations);
        services.AddSingleton(bothTenantsObserved);
        services.AddScoped<IDemandForecastService, RecordingDemandForecastService>();

        await using var provider = services.BuildServiceProvider();
        var runner = new TenantForecastRunner(provider.GetRequiredService<IServiceScopeFactory>());
        var backgroundService = new ForecastBackgroundService(
            runner,
            Options.Create(new TenantOptions
            {
                HostTenants = new Dictionary<string, string>
                {
                    ["a.example"] = tenantAId,
                    ["a-alias.example"] = tenantAId,
                    ["b.example"] = tenantBId
                }
            }),
            Options.Create(new ForecastingOptions { Enabled = true }),
            NullLogger<ForecastBackgroundService>.Instance);

        try
        {
            await backgroundService.StartAsync(CancellationToken.None);
            await bothTenantsObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await backgroundService.StopAsync(CancellationToken.None);
        }

        var observed = observations.ToArray();
        observed.Should().HaveCount(2);
        var observedByTenant = observed.ToDictionary(observation => observation.TenantIdAtResolution);

        observedByTenant.Keys.Should().BeEquivalentTo(tenantAId, tenantBId);
        observedByTenant[tenantAId].DatabaseTenantIdAtResolution.Should().Be(tenantAId);
        observedByTenant[tenantAId].VisibleItemIds.Should().Equal(tenantAItemId);
        observedByTenant[tenantBId].DatabaseTenantIdAtResolution.Should().Be(tenantBId);
        observedByTenant[tenantBId].VisibleItemIds.Should().Equal(tenantBItemId);
    }

    private async Task<int> SeedItemAsync(string tenantId, string itemCode)
    {
        await using var context = fixture.CreateContext(tenantId);
        var item = new Item
        {
            ItemCode = itemCode,
            Description = $"Synthetic hosted forecast item for {tenantId}",
            Rate = 1m
        };
        context.Items.Add(item);
        await context.SaveChangesAsync();
        return item.Id;
    }

    private sealed record ForecastQueryObservation(
        string TenantIdAtResolution,
        string DatabaseTenantIdAtResolution,
        IReadOnlyList<int> VisibleItemIds);

    private sealed class RecordingDemandForecastService(
        InventoryDbContext context,
        ITenantContext tenantContext,
        ConcurrentQueue<ForecastQueryObservation> observations,
        TaskCompletionSource bothTenantsObserved) : IDemandForecastService
    {
        private readonly string _tenantIdAtResolution = tenantContext.TenantId;
        private readonly string _databaseTenantIdAtResolution = context.CurrentTenantId;

        public Task<DemandForecastResult> ForecastDemandAsync(int itemId, int horizonDays = 30) =>
            throw new NotSupportedException();

        public Task<DemandForecastResult> ForecastDemandForCompaniesAsync(
            int itemId,
            int horizonDays,
            IReadOnlyCollection<int> companyIds) => throw new NotSupportedException();

        public async Task<IReadOnlyList<DemandForecastResult>> ForecastAllItemsAsync(int horizonDays = 30)
        {
            var visibleItemIds = await context.Items
                .AsNoTracking()
                .OrderBy(item => item.Id)
                .Select(item => item.Id)
                .ToListAsync();

            observations.Enqueue(new ForecastQueryObservation(
                _tenantIdAtResolution,
                _databaseTenantIdAtResolution,
                visibleItemIds));
            if (observations.Count >= 2)
            {
                bothTenantsObserved.TrySetResult();
            }

            return [];
        }

        public Task<IReadOnlyList<DemandForecastResult>> ForecastAllItemsForCompaniesAsync(
            int horizonDays,
            IReadOnlyCollection<int> companyIds) => throw new NotSupportedException();
    }
}
