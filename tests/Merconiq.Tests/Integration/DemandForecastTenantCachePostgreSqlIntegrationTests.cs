using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Options;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Tests.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class DemandForecastTenantCachePostgreSqlIntegrationTests(PostgreSqlIntegrationFixture fixture)
{
    private const int ForecastHorizonDays = 5;
    private const int HistoricalSaleDays = 7;

    [PostgreSqlFact]
    public async Task Shared_cache_keeps_all_item_forecasts_isolated_between_tenants()
    {
        fixture.EnsureEnabled();

        var suffix = Guid.NewGuid().ToString("N");
        var tenantAId = $"forecast-cache-a-{suffix}";
        var tenantBId = $"forecast-cache-b-{suffix}";
        var tenantAItem = await SeedTenantHistoryAsync(tenantAId, $"FCA-{suffix}", quantityPerDay: 3);
        var tenantBItem = await SeedTenantHistoryAsync(tenantBId, $"FCB-{suffix}", quantityPerDay: 17);

        using var sharedCache = new MemoryCache(new MemoryCacheOptions());
        await using var contextA = fixture.CreateContext(tenantAId);
        await using var contextB = fixture.CreateContext(tenantBId);
        var serviceA = CreateService(contextA, tenantAId, sharedCache);
        var serviceB = CreateService(contextB, tenantBId, sharedCache);

        var tenantAResults = await serviceA.ForecastAllItemsAsync(ForecastHorizonDays);
        var tenantBResults = await serviceB.ForecastAllItemsAsync(ForecastHorizonDays);

        tenantAResults.Should().ContainSingle();
        tenantBResults.Should().ContainSingle();

        var tenantAForecast = tenantAResults[0];
        var tenantBForecast = tenantBResults[0];

        tenantAForecast.ItemId.Should().Be(tenantAItem.ItemId);
        tenantAForecast.ItemName.Should().Be(tenantAItem.ItemCode);
        tenantAForecast.TotalHistoricalDays.Should().Be(HistoricalSaleDays);
        tenantAForecast.AverageDailyDemand.Should().Be(3);
        tenantAForecast.ForecastedValues.Should()
            .Equal(Enumerable.Repeat(3f, ForecastHorizonDays));

        tenantBForecast.ItemId.Should().Be(tenantBItem.ItemId);
        tenantBForecast.ItemName.Should().Be(tenantBItem.ItemCode);
        tenantBForecast.TotalHistoricalDays.Should().Be(HistoricalSaleDays);
        tenantBForecast.AverageDailyDemand.Should().Be(17);
        tenantBForecast.ForecastedValues.Should()
            .Equal(Enumerable.Repeat(17f, ForecastHorizonDays));

        tenantAForecast.ItemId.Should().NotBe(tenantBForecast.ItemId);
        tenantAResults.Should().NotBeSameAs(tenantBResults);
        (await serviceA.ForecastAllItemsAsync(ForecastHorizonDays)).Should().BeSameAs(tenantAResults);
        (await serviceB.ForecastAllItemsAsync(ForecastHorizonDays)).Should().BeSameAs(tenantBResults);
    }

    private async Task<SeededForecastItem> SeedTenantHistoryAsync(
        string tenantId,
        string itemCode,
        int quantityPerDay)
    {
        await using var context = fixture.CreateContext(tenantId);
        var item = new Item
        {
            ItemCode = itemCode,
            Description = $"Synthetic forecast history for {tenantId}",
            Rate = 1m
        };
        var location = new Location { Name = $"forecast-location-{Guid.NewGuid():N}" };

        context.Items.Add(item);
        context.Locations.Add(location);
        await context.SaveChangesAsync();

        var today = DateTime.UtcNow.Date;
        context.StockTransactions.AddRange(Enumerable.Range(1, HistoricalSaleDays).Select(day => new StockTransaction
        {
            ItemId = item.Id,
            FromLocationId = location.Id,
            TransactionType = TransactionType.Sell,
            TransactionDate = today.AddDays(-day),
            Quantity = quantityPerDay
        }));
        await context.SaveChangesAsync();

        return new SeededForecastItem(item.Id, itemCode);
    }

    private static DemandForecastService CreateService(
        InventoryDbContext context,
        string tenantId,
        IMemoryCache sharedCache) =>
        new(
            new Repository<StockTransaction>(context),
            new Repository<Item>(context),
            NullLogger<DemandForecastService>.Instance,
            sharedCache,
            new TestTenantContext(tenantId),
            Options.Create(new ForecastingOptions()));

    private sealed record SeededForecastItem(int ItemId, string ItemCode);
}
