using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Integration;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class AuthenticatedForecastCompanyCachePostgreSqlApiTests(
    PostgreSqlIntegrationFixture fixture)
{
    private const int ForecastHorizonDays = 5;
    private const int HistoricalSaleDays = 7;
    private const string TenantId = "test-tenant";

    [PostgreSqlFact]
    public async Task Authenticated_forecasts_keep_company_results_isolated_in_the_shared_application_cache()
    {
        fixture.EnsureEnabled();

        var suffix = Guid.NewGuid().ToString("N");
        var seeded = await SeedCompanyForecastHistoryAsync(suffix);
        using var factory = new PostgreSqlCompanyApiFactory(
            fixture,
            applicationName: "merconiq-forecast-company-cache-test");

        var companyAUser = await CreateViewOnlyUserAsync(fixture, factory, suffix, "a", seeded.CompanyAId);
        var companyBUser = await CreateViewOnlyUserAsync(fixture, factory, suffix, "b", seeded.CompanyBId);
        using var companyAClient = factory.CreateAuthenticatedClient(companyAUser, "RestrictedAuditor");
        using var companyBClient = factory.CreateAuthenticatedClient(companyBUser, "RestrictedAuditor");

        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        firstScope.ServiceProvider.GetRequiredService<IMemoryCache>()
            .Should().BeSameAs(secondScope.ServiceProvider.GetRequiredService<IMemoryCache>());

        // Cold, simultaneous requests for the same item must use distinct company scopes.
        var concurrentSharedItemForecasts = await Task.WhenAll(
            GetItemForecastAsync(companyAClient, seeded.SharedItemId),
            GetItemForecastAsync(companyBClient, seeded.SharedItemId));
        AssertForecast(concurrentSharedItemForecasts[0], seeded.SharedItemId, "SHARED-", 3);
        AssertForecast(concurrentSharedItemForecasts[1], seeded.SharedItemId, "SHARED-", 17);

        var companyAForecasts = await GetAllForecastsAsync(companyAClient);
        companyAForecasts.Select(forecast => forecast.ItemId).Should().BeEquivalentTo(
            [seeded.SharedItemId, seeded.CompanyAItemId],
            "company A must not receive a forecast row for company B's item");
        AssertForecast(companyAForecasts, seeded.SharedItemId, "SHARED-", 3);
        AssertForecast(companyAForecasts, seeded.CompanyAItemId, "COMPANY-A-", 5);

        var companyBForecasts = await GetAllForecastsAsync(companyBClient);
        companyBForecasts.Select(forecast => forecast.ItemId).Should().BeEquivalentTo(
            [seeded.SharedItemId, seeded.CompanyBItemId],
            "company B must not receive a forecast row for company A's item");
        AssertForecast(companyBForecasts, seeded.SharedItemId, "SHARED-", 17);
        AssertForecast(companyBForecasts, seeded.CompanyBItemId, "COMPANY-B-", 23);

        var companyASharedItemForecast = await GetItemForecastAsync(companyAClient, seeded.SharedItemId);
        AssertForecast(companyASharedItemForecast, seeded.SharedItemId, "SHARED-", 3);

        var companyBSharedItemForecast = await GetItemForecastAsync(companyBClient, seeded.SharedItemId);
        AssertForecast(companyBSharedItemForecast, seeded.SharedItemId, "SHARED-", 17);

        // Repeat after both company scopes have populated the application cache.
        AssertForecast(await GetItemForecastAsync(companyAClient, seeded.SharedItemId),
            seeded.SharedItemId, "SHARED-", 3);
        var repeatedCompanyBForecasts = await GetAllForecastsAsync(companyBClient);
        AssertForecast(repeatedCompanyBForecasts, seeded.SharedItemId, "SHARED-", 17);
        repeatedCompanyBForecasts.Select(forecast => forecast.ItemId).Should().NotContain(seeded.CompanyAItemId);
    }

    private async Task<SeededCompanyForecast> SeedCompanyForecastHistoryAsync(string suffix)
    {
        await using var context = fixture.CreateContext(TenantId);
        var companyA = new Company
        {
            Code = $"FC-A-{suffix[..10]}",
            LegalName = "Forecast cache company A",
            BaseCurrency = "USD"
        };
        var companyB = new Company
        {
            Code = $"FC-B-{suffix[..10]}",
            LegalName = "Forecast cache company B",
            BaseCurrency = "USD"
        };
        var branchA = new Branch { Company = companyA, Code = "FC-A", Name = "Forecast branch A" };
        var branchB = new Branch { Company = companyB, Code = "FC-B", Name = "Forecast branch B" };
        var locationA = new Location { Branch = branchA, Name = "Forecast location A" };
        var locationB = new Location { Branch = branchB, Name = "Forecast location B" };
        var sharedItem = NewItem($"SHARED-{suffix[..10]}");
        var companyAItem = NewItem($"COMPANY-A-{suffix[..10]}");
        var companyBItem = NewItem($"COMPANY-B-{suffix[..10]}");

        context.AddRange(companyA, companyB, branchA, branchB, locationA, locationB,
            sharedItem, companyAItem, companyBItem);
        await context.SaveChangesAsync();

        context.StockTransactions.AddRange(
            CreateSales(sharedItem.Id, locationA.Id, 3)
                .Concat(CreateSales(sharedItem.Id, locationB.Id, 17))
                .Concat(CreateSales(companyAItem.Id, locationA.Id, 5))
                .Concat(CreateSales(companyBItem.Id, locationB.Id, 23)));
        await context.SaveChangesAsync();

        return new SeededCompanyForecast(
            companyA.Id,
            companyB.Id,
            sharedItem.Id,
            companyAItem.Id,
            companyBItem.Id);
    }

    private static Item NewItem(string itemCode) => new()
    {
        ItemCode = itemCode,
        Description = "Synthetic company-scoped forecast test item",
        Rate = 1m
    };

    private static IEnumerable<StockTransaction> CreateSales(int itemId, int locationId, int quantityPerDay)
    {
        var today = DateTime.UtcNow.Date;
        return Enumerable.Range(1, HistoricalSaleDays).Select(day => new StockTransaction
        {
            ItemId = itemId,
            FromLocationId = locationId,
            TransactionType = TransactionType.Sell,
            TransactionDate = today.AddDays(-day),
            Quantity = quantityPerDay
        });
    }

    private static async Task<ApplicationUser> CreateViewOnlyUserAsync(
        PostgreSqlIntegrationFixture fixture,
        PostgreSqlCompanyApiFactory factory,
        string suffix,
        string userSuffix,
        int companyId)
    {
        var user = await factory.EnsurePersonaUserAsync(
            "RestrictedAuditor",
            $"forecast-{suffix}-{userSuffix}");
        await using var context = fixture.CreateContext(TenantId);
        context.CompanyMemberships.Add(new CompanyMembership
        {
            CompanyId = companyId,
            UserId = user.Id,
            Capabilities = CompanyCapability.View,
            IsActive = true
        });
        await context.SaveChangesAsync();
        return user;
    }

    private static async Task<IReadOnlyList<DemandForecastResult>> GetAllForecastsAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"/api/v1/forecast?horizon={ForecastHorizonDays}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<List<DemandForecastResult>>>();
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().NotBeNull();
        return envelope.Data!;
    }

    private static async Task<DemandForecastResult> GetItemForecastAsync(HttpClient client, int itemId)
    {
        using var response = await client.GetAsync(
            $"/api/v1/forecast/{itemId}?horizon={ForecastHorizonDays}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<DemandForecastResult>>();
        envelope.Should().NotBeNull();
        envelope!.Success.Should().BeTrue();
        envelope.Data.Should().NotBeNull();
        return envelope.Data!;
    }

    private static void AssertForecast(
        IReadOnlyCollection<DemandForecastResult> forecasts,
        int itemId,
        string itemCodePrefix,
        float averageDailyDemand) =>
        AssertForecast(forecasts.Should().ContainSingle(forecast => forecast.ItemId == itemId).Subject,
            itemId,
            itemCodePrefix,
            averageDailyDemand);

    private static void AssertForecast(
        DemandForecastResult forecast,
        int itemId,
        string itemCodePrefix,
        float averageDailyDemand)
    {
        forecast.ItemId.Should().Be(itemId);
        forecast.ItemName.Should().StartWith(itemCodePrefix);
        forecast.TotalHistoricalDays.Should().Be(HistoricalSaleDays);
        forecast.AverageDailyDemand.Should().Be(averageDailyDemand);
        forecast.ForecastedValues.Should().Equal(Enumerable.Repeat(averageDailyDemand, ForecastHorizonDays));
    }

    private sealed record SeededCompanyForecast(
        int CompanyAId,
        int CompanyBId,
        int SharedItemId,
        int CompanyAItemId,
        int CompanyBItemId);
}
