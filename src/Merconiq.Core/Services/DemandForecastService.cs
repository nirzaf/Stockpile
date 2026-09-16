using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.ML;
using Microsoft.ML.Transforms.TimeSeries;

namespace Merconiq.Core.Services;

/// <summary>
/// Demand forecast service. Uses the configured forecasting implementation per item.
/// Production defaults to the platform-independent managed moving average; ML.NET's SSA
/// implementation remains available as an explicit opt-in. Results are cached in memory
/// until the process restarts.
/// </summary>
public class DemandForecastService : IDemandForecastService
{
    private readonly IRepository<StockTransaction> _txRepo;
    private readonly IRepository<Item> _itemRepo;
    private readonly ILogger<DemandForecastService> _logger;
    private readonly IMemoryCache _cache;
    private readonly ITenantContext _tenantContext;
    private readonly ForecastingOptions _forecastingOptions;

    private const int MinDataPoints = 5;
    private const int DefaultWindowSize = 7;
    private const float ConfidenceLevel = 0.95f;
    private static readonly TimeSpan ForecastCacheDuration = TimeSpan.FromHours(4);

    public DemandForecastService(
        IRepository<StockTransaction> txRepo,
        IRepository<Item> itemRepo,
        ILogger<DemandForecastService> logger,
        IMemoryCache cache,
        ITenantContext tenantContext,
        IOptions<ForecastingOptions> forecastingOptions)
    {
        _txRepo = txRepo;
        _itemRepo = itemRepo;
        _logger = logger;
        _cache = cache;
        _tenantContext = tenantContext;
        _forecastingOptions = forecastingOptions.Value;
    }

    /// <inheritdoc />
    public Task<DemandForecastResult> ForecastDemandAsync(int itemId, int horizonDays = 30) =>
        ForecastDemandForScopeAsync(itemId, horizonDays, null);

    public Task<DemandForecastResult> ForecastDemandForCompaniesAsync(
        int itemId,
        int horizonDays,
        IReadOnlyCollection<int> companyIds) =>
        ForecastDemandForScopeAsync(itemId, horizonDays, companyIds);

    private async Task<DemandForecastResult> ForecastDemandForScopeAsync(
        int itemId,
        int horizonDays,
        IReadOnlyCollection<int>? companyIds)
    {
        var cacheKey = TenantCacheKeys.ForecastForItem(_tenantContext.TenantId, itemId, horizonDays, companyIds);
        if (_cache.TryGetValue(cacheKey, out DemandForecastResult? cachedResult) && cachedResult != null)
        {
            _logger.LogDebug("Returning cached forecast for item {ItemId}", itemId);
            return cachedResult;
        }

        var result = await GenerateForecastAsync(itemId, horizonDays, companyIds);
        _cache.Set(cacheKey, result, ForecastCacheDuration);
        return result;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DemandForecastResult>> ForecastAllItemsAsync(int horizonDays = 30) =>
        ForecastAllItemsForScopeAsync(horizonDays, null);

    public Task<IReadOnlyList<DemandForecastResult>> ForecastAllItemsForCompaniesAsync(
        int horizonDays,
        IReadOnlyCollection<int> companyIds) =>
        ForecastAllItemsForScopeAsync(horizonDays, companyIds);

    private async Task<IReadOnlyList<DemandForecastResult>> ForecastAllItemsForScopeAsync(
        int horizonDays,
        IReadOnlyCollection<int>? companyIds)
    {
        var cacheKey = TenantCacheKeys.ForecastForAllItems(_tenantContext.TenantId, horizonDays, companyIds);
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<DemandForecastResult>? cachedResult) && cachedResult != null)
        {
            _logger.LogDebug("Returning cached forecasts for all items");
            return cachedResult;
        }

        var results = await GenerateForecastAllItemsAsync(horizonDays, companyIds);
        _cache.Set(cacheKey, results, ForecastCacheDuration);
        return results;
    }

    private async Task<DemandForecastResult> GenerateForecastAsync(
        int itemId,
        int horizonDays,
        IReadOnlyCollection<int>? companyIds)
    {
        var item = (await _itemRepo.FindAsync(i => i.Id == itemId)).FirstOrDefault();

        var result = new DemandForecastResult
        {
            ItemId = itemId,
            ItemName = item?.ItemCode ?? $"Item #{itemId}",
            ForecastHorizonDays = horizonDays,
            ForecastingImplementation = _forecastingOptions.Implementation
        };

        var transactions = await _txRepo.FindAsync(t =>
            t.ItemId == itemId &&
            t.TransactionType == TransactionType.Sell &&
            (companyIds == null || (t.FromLocation.Branch != null &&
                companyIds.Contains(t.FromLocation.Branch.CompanyId))));

        var dailyDemand = DemandForecastDataPreparation.BuildDailyDemand(transactions);

        if (dailyDemand.Count < MinDataPoints)
        {
            _logger.LogWarning("Insufficient data for item {ItemId}: {Count} days (need {Min})",
                itemId, dailyDemand.Count, MinDataPoints);
            return result;
        }

        result.TotalHistoricalDays = dailyDemand.Count;
        result.AverageDailyDemand = dailyDemand.Average(d => d.Quantity);

        if (string.Equals(_forecastingOptions.Implementation,
                ForecastingImplementations.ManagedMovingAverage,
                StringComparison.OrdinalIgnoreCase))
        {
            result.ForecastedValues = Enumerable.Repeat(result.AverageDailyDemand, horizonDays).ToList();
            _logger.LogInformation(
                "Demand forecast generated using the managed moving-average implementation for item {ItemId}: {Horizon}d horizon from {Days}d history",
                itemId, horizonDays, dailyDemand.Count);
            return result;
        }

        if (string.Equals(_forecastingOptions.Implementation,
                ForecastingImplementations.Ssa,
                StringComparison.OrdinalIgnoreCase))
        {
            GenerateSsaForecast(result, dailyDemand, horizonDays, itemId);
            return result;
        }

        throw new InvalidOperationException(
            $"Unsupported forecasting implementation '{_forecastingOptions.Implementation}'.");
    }

    private void GenerateSsaForecast(
        DemandForecastResult result,
        IReadOnlyList<DailyDemandObservation> dailyDemand,
        int horizonDays,
        int itemId)
    {
        // Seed ML.NET with a fixed value (42) so that two forecast runs over the same
        // history produce identical results — important for reproducible batch jobs and tests.
        var mlContext = new MLContext(seed: 42);

        var values = dailyDemand.Select(d => d.Quantity).ToArray();
        var data = values.Select(v => new DemandDataPoint { Quantity = v }).ToList();

        var dataView = mlContext.Data.LoadFromEnumerable(data);

        // SSA window: roughly half the series length, capped at 7 days (a week). SSA
        // handles seasonality better than ARIMA with limited history, which is the
        // realistic case for a small business that just started tracking transactions.
        var windowSize = Math.Min(DefaultWindowSize, values.Length / 2);
        if (windowSize < 2) windowSize = 2;

        try
        {
            var pipeline = mlContext.Forecasting.ForecastBySsa(
                outputColumnName: nameof(DemandPrediction.ForecastedQuantity),
                inputColumnName: nameof(DemandDataPoint.Quantity),
                windowSize: windowSize,
                seriesLength: values.Length,
                trainSize: values.Length,
                horizon: horizonDays,
                confidenceLevel: ConfidenceLevel,
                confidenceLowerBoundColumn: nameof(DemandPrediction.ConfidenceLower),
                confidenceUpperBoundColumn: nameof(DemandPrediction.ConfidenceUpper));

            var model = pipeline.Fit(dataView);
            var engine = model.CreateTimeSeriesEngine<DemandDataPoint, DemandPrediction>(mlContext);
            var prediction = engine.Predict(horizonDays);

            result.ForecastedValues = prediction.ForecastedQuantity?.ToList() ?? [];
            result.ConfidenceLower = prediction.ConfidenceLower?.ToList() ?? [];
            result.ConfidenceUpper = prediction.ConfidenceUpper?.ToList() ?? [];

            _logger.LogInformation("Demand forecast generated for item {ItemId}: {Horizon}d horizon from {Days}d history",
                itemId, horizonDays, dailyDemand.Count);
        }
        catch (Exception ex)
        {
            // SSA is opt-in because its native runtime must be installed by the deployment.
            // Never silently switch to another model: the caller needs an actionable failure
            // and the configured implementation must remain truthful in the response.
            _logger.LogError(ex,
                "ML.NET SSA forecast failed for item {ItemId}; the configured SSA runtime is unavailable or rejected the input",
                itemId);
            throw;
        }
    }

    private async Task<IReadOnlyList<DemandForecastResult>> GenerateForecastAllItemsAsync(
        int horizonDays,
        IReadOnlyCollection<int>? companyIds)
    {
        var items = await _itemRepo.GetAllAsync();
        
        var results = new List<DemandForecastResult>();
        foreach (var item in items)
        {
            try
            {
                var forecast = companyIds is null
                    ? await ForecastDemandAsync(item.Id, horizonDays)
                    : await ForecastDemandForCompaniesAsync(item.Id, horizonDays, companyIds);
                if (forecast.ForecastedValues.Count > 0)
                {
                    results.Add(forecast);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Forecast failed for item {ItemId}, skipping", item.Id);
            }
        }

        return results;
    }

    private class DemandDataPoint
    {
        public float Quantity { get; set; }
    }

    private class DemandPrediction
    {
        public float[]? ForecastedQuantity { get; set; }
        public float[]? ConfidenceLower { get; set; }
        public float[]? ConfidenceUpper { get; set; }
    }
}
