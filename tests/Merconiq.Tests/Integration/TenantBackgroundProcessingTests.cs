using FluentAssertions;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Options;
using Merconiq.Web.BackgroundServices;
using Merconiq.Web.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Merconiq.Tests.Integration;

public class TenantBackgroundProcessingTests
{
    [Fact]
    public async Task Forecast_runner_creates_one_explicit_scope_per_tenant()
    {
        var observedTenants = new List<string>();
        var services = new ServiceCollection();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(provider =>
            provider.GetRequiredService<TenantContext>());
        services.AddScoped<IDemandForecastService>(provider =>
            new RecordingForecastService(provider.GetRequiredService<ITenantContext>(), observedTenants));

        await using var provider = services.BuildServiceProvider();
        var runner = new TenantForecastRunner(provider.GetRequiredService<IServiceScopeFactory>());

        await runner.RunAsync("tenant-a", CancellationToken.None);
        await runner.RunAsync("tenant-b", CancellationToken.None);

        observedTenants.Should().Equal("tenant-a", "tenant-b");
    }

    [Fact]
    public async Task Forecast_background_service_skips_tenants_when_forecasting_is_disabled()
    {
        var runner = new RecordingTenantForecastRunner();
        var service = new ForecastBackgroundService(
            runner,
            Options.Create(new TenantOptions
            {
                HostTenants = new Dictionary<string, string> { ["a.example"] = "tenant-a" }
            }),
            Options.Create(new ForecastingOptions { Enabled = false }),
            NullLogger<ForecastBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        runner.ObservedTenants.Should().BeEmpty();
    }

    [Fact]
    public void Tenant_options_enumerate_distinct_configured_tenants()
    {
        var options = new TenantOptions
        {
            HostTenants = new Dictionary<string, string>
            {
                ["a.example"] = "tenant-a",
                ["a-alt.example"] = "tenant-a",
                ["b.example"] = "tenant-b"
            }
        };

        options.GetTenantIds().Should().BeEquivalentTo("tenant-a", "tenant-b");
    }

    private sealed class RecordingForecastService(
        ITenantContext tenantContext,
        ICollection<string> observedTenants) : IDemandForecastService
    {
        public Task<DemandForecastResult> ForecastDemandAsync(int itemId, int horizonDays = 30) =>
            throw new NotSupportedException();

        public Task<DemandForecastResult> ForecastDemandForCompaniesAsync(
            int itemId,
            int horizonDays,
            IReadOnlyCollection<int> companyIds) => throw new NotSupportedException();

        public Task<IReadOnlyList<DemandForecastResult>> ForecastAllItemsAsync(int horizonDays = 30)
        {
            observedTenants.Add(tenantContext.TenantId);
            return Task.FromResult<IReadOnlyList<DemandForecastResult>>([]);
        }

        public Task<IReadOnlyList<DemandForecastResult>> ForecastAllItemsForCompaniesAsync(
            int horizonDays,
            IReadOnlyCollection<int> companyIds) => throw new NotSupportedException();
    }

    private sealed class RecordingTenantForecastRunner : ITenantForecastRunner
    {
        public List<string> ObservedTenants { get; } = [];

        public Task RunAsync(string tenantId, CancellationToken cancellationToken)
        {
            ObservedTenants.Add(tenantId);
            return Task.CompletedTask;
        }
    }
}
