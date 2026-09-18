using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.BackgroundServices;
using Merconiq.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class TenantForecastRunnerPostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    [PostgreSqlFact]
    public async Task Forecast_runner_queries_only_the_explicitly_selected_tenant_items()
    {
        fixture.EnsureEnabled();

        var suffix = Guid.NewGuid().ToString("N");
        var tenantAId = $"forecast-tenant-a-{suffix}";
        var tenantBId = $"forecast-tenant-b-{suffix}";
        var tenantAItemId = await SeedItemAsync(tenantAId, $"A-{suffix}");
        var tenantBItemId = await SeedItemAsync(tenantBId, $"B-{suffix}");

        var observations = new List<ForecastQueryObservation>();
        var services = new ServiceCollection();
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<TenantContext>());
        services.AddDbContext<InventoryDbContext>(options =>
            options.UseNpgsql(fixture.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddSingleton<ICollection<ForecastQueryObservation>>(observations);
        services.AddScoped<IDemandForecastService, RecordingDemandForecastService>();

        await using var provider = services.BuildServiceProvider();
        var runner = new TenantForecastRunner(provider.GetRequiredService<IServiceScopeFactory>());

        await runner.RunAsync(tenantAId, CancellationToken.None);
        await runner.RunAsync(tenantBId, CancellationToken.None);

        observations.Should().HaveCount(2);
        observations.Select(observation => observation.TenantIdAtResolution)
            .Should().Equal([tenantAId, tenantBId]);
        observations.Select(observation => observation.DatabaseTenantIdAtResolution)
            .Should().Equal([tenantAId, tenantBId]);
        observations[0].VisibleItemIds.Should().Equal([tenantAItemId]);
        observations[1].VisibleItemIds.Should().Equal([tenantBItemId]);
    }

    private async Task<int> SeedItemAsync(string tenantId, string itemCode)
    {
        await using var context = fixture.CreateContext(tenantId);
        var item = new Item
        {
            ItemCode = itemCode,
            Description = $"Synthetic forecast item for {tenantId}",
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
        ICollection<ForecastQueryObservation> observations) : IDemandForecastService
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

            observations.Add(new ForecastQueryObservation(
                _tenantIdAtResolution,
                _databaseTenantIdAtResolution,
                visibleItemIds));
            return [];
        }

        public Task<IReadOnlyList<DemandForecastResult>> ForecastAllItemsForCompaniesAsync(
            int horizonDays,
            IReadOnlyCollection<int> companyIds) => throw new NotSupportedException();
    }
}
