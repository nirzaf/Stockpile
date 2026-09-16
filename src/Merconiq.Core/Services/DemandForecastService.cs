using Merconiq.Core.Entities;
using Merconiq.Core.Exceptions;
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
    private readonly TimeProvider _timeProvider;

    private const int DefaultWindowSize = 7;
    private const float ConfidenceLevel = 0.95f;
    private static readonly TimeSpan ForecastCacheDuration = TimeSpan.FromHours(4);

    public DemandForecastService(
        IRepository<StockTransaction> txRepo,
        IRepository<Item> itemRepo,
        ILogger<DemandForecastService> logger,
        IMemoryCache cache,
        ITenantContext tenantContext,
        IOptions<ForecastingOptions> forecastingOptions,
        TimeProvider? timeProvider = null)
    {
        _txRepo = txRepo;
        _itemRepo = itemRepo;
        _logger = logger;
        _cache = cache;
        _tenantContext = tenantContext;
        _forecastingOptions = forecastingOptions.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (!ForecastingOptions.HasValidResourceLimits(_forecastingOptions))
        {
            throw new ArgumentException(
                $"Forecasting limits must set {nameof(ForecastingOptions.MaxForecastHorizonDays)} between 1 and {ForecastingOptions.AbsoluteMaxForecastHorizonDays}, {nameof(ForecastingOptions.MaxHistoricalDays)} between {ForecastingOptions.MinimumHistoricalDays} and {ForecastingOptions.AbsoluteMaxHistoricalDays}, {nameof(ForecastingOptions.MaxHistoricalTransactionsPerForecast)} between 1 and {ForecastingOptions.AbsoluteMaxHistoricalTransactionsPerForecast}, and {nameof(ForecastingOptions.MaxItemsPerAllItemsForecast)} between 1 and {ForecastingOptions.AbsoluteMaxItemsPerAllItemsForecast}.",
                nameof(forecastingOptions));
        }

        if (!ForecastingImplementations.IsSupported(_forecastingOptions.Implementation))
        {
            throw new ArgumentException(
                $"Unsupported forecasting implementation '{_forecastingOptions.Implementation}'.",
                nameof(forecastingOptions));
        }
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
        ValidateHorizon(horizonDays);
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
        ValidateHorizon(horizonDays);
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
        var generatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        var asOfDate = generatedAt.Date;
        var dataWindowStart = asOfDate.AddDays(1 - _forecastingOptions.MaxHistoricalDays);
        var dataWindowEndExclusive = asOfDate.AddDays(1);
        var implementation = NormalizeImplementation(_forecastingOptions.Implementation);

        var result = new DemandForecastResult
        {
            ItemId = itemId,
            ItemName = item?.ItemCode ?? $"Item #{itemId}",
            ForecastHorizonDays = horizonDays,
            ForecastingImplementation = implementation,
            ForecastingImplementationVersion = GetImplementationVersion(implementation),
            MaxForecastHorizonDays = _forecastingOptions.MaxForecastHorizonDays,
            MaxHistoricalDays = _forecastingOptions.MaxHistoricalDays,
            GeneratedAt = generatedAt,
            KnownLimitations = GetKnownLimitations(implementation)
        };

        var transactions = (await _txRepo.FindPageAsync(
            t => t.ItemId == itemId &&
                t.TransactionType == TransactionType.Sell &&
                t.TransactionDate >= dataWindowStart &&
                t.TransactionDate < dataWindowEndExclusive &&
                (companyIds == null || (t.FromLocation.Branch != null &&
                    companyIds.Contains(t.FromLocation.Branch.CompanyId))),
            query => query.OrderBy(t => t.TransactionDate).ThenBy(t => t.Id),
            _forecastingOptions.MaxHistoricalTransactionsPerForecast + 1)).ToList();

        RejectWhenOverLimit(
            transactions.Count,
            _forecastingOptions.MaxHistoricalTransactionsPerForecast,
            nameof(ForecastingOptions.MaxHistoricalTransactionsPerForecast));

        // Keep the calendar series bounded even when a repository implementation or test
        // double does not apply its predicate server-side.
        var boundedTransactions = transactions.Where(transaction =>
            transaction.TransactionDate >= dataWindowStart &&
            transaction.TransactionDate < dataWindowEndExclusive);
        var dailyDemand = DemandForecastDataPreparation.BuildDailyDemand(boundedTransactions);
        result.TotalHistoricalDays = dailyDemand.Count;
        if (dailyDemand.Count > 0)
        {
            result.DataWindowStartDate = DateOnly.FromDateTime(dailyDemand[0].Date);
            result.DataWindowEndDate = DateOnly.FromDateTime(dailyDemand[^1].Date);
        }

        if (dailyDemand.Count < ForecastingOptions.MinimumHistoricalDays)
        {
            _logger.LogWarning("Insufficient data for item {ItemId}: {Count} days (need {Min})",
                itemId, dailyDemand.Count, ForecastingOptions.MinimumHistoricalDays);
            return result;
        }

        result.AverageDailyDemand = dailyDemand.Average(d => d.Quantity);

        if (string.Equals(implementation,
                ForecastingImplementations.ManagedMovingAverage,
                StringComparison.OrdinalIgnoreCase))
        {
            result.ForecastedValues = Enumerable.Repeat(result.AverageDailyDemand, horizonDays).ToList();
            _logger.LogInformation(
                "Demand forecast generated using the managed moving-average implementation for item {ItemId}: {Horizon}d horizon from {Days}d history",
                itemId, horizonDays, dailyDemand.Count);
            return result;
        }

        if (string.Equals(implementation,
                ForecastingImplementations.Ssa,
                StringComparison.OrdinalIgnoreCase))
        {
            GenerateSsaForecast(result, dailyDemand, horizonDays, itemId);
            return result;
        }

        throw new InvalidOperationException(
            $"Unsupported forecasting implementation '{_forecastingOptions.Implementation}'.");
    }

    private void ValidateHorizon(int horizonDays)
    {
        if (horizonDays < 1 || horizonDays > _forecastingOptions.MaxForecastHorizonDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(horizonDays),
                horizonDays,
                $"Forecast horizon must be between 1 and {_forecastingOptions.MaxForecastHorizonDays} days.");
        }
    }

    private static string NormalizeImplementation(string implementation) =>
        string.Equals(implementation, ForecastingImplementations.ManagedMovingAverage, StringComparison.OrdinalIgnoreCase)
            ? ForecastingImplementations.ManagedMovingAverage
            : ForecastingImplementations.Ssa;

    private static string GetImplementationVersion(string implementation) =>
        string.Equals(implementation, ForecastingImplementations.ManagedMovingAverage, StringComparison.Ordinal)
            ? ForecastingImplementations.ManagedMovingAverageVersion
            : $"Microsoft.ML.TimeSeries {typeof(SsaForecastingEstimator).Assembly.GetName().Version?.ToString() ?? "unknown"}";

    private static IReadOnlyList<string> GetKnownLimitations(string implementation)
    {
        var limitations = new List<string>
        {
            "Only recorded sell movements contribute to demand; the transaction model has no distinct return movement, so returns cannot be netted out.",
            "Stockout and lost-sales observations are not recorded; zero recorded sales cannot distinguish no demand from unavailable stock.",
            "Missing calendar dates between the first and latest recorded sale are filled with zero; dates after the latest sale are not added.",
            "Forecasts enforce configured hard limits on matching historical sell rows and all-item catalog size; over-limit requests are rejected rather than truncated."
        };

        if (string.Equals(implementation, ForecastingImplementations.ManagedMovingAverage, StringComparison.Ordinal))
        {
            limitations.Add("The managed moving average repeats the historical mean and does not model trend or seasonality.");
        }
        else
        {
            limitations.Add("SSA is an explicit opt-in and requires compatible native runtime dependencies; failures are returned without silently falling back to another model.");
        }

        return limitations;
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
        var items = (await _itemRepo.FindPageAsync(
            item => companyIds == null || item.StockTransactions.Any(transaction =>
                transaction.TransactionType == TransactionType.Sell &&
                transaction.FromLocation.Branch != null &&
                companyIds.Contains(transaction.FromLocation.Branch.CompanyId)),
            query => query.OrderBy(item => item.Id),
            _forecastingOptions.MaxItemsPerAllItemsForecast + 1)).ToList();

        RejectWhenOverLimit(
            items.Count,
            _forecastingOptions.MaxItemsPerAllItemsForecast,
            nameof(ForecastingOptions.MaxItemsPerAllItemsForecast));

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
            catch (ForecastResourceLimitExceededException)
            {
                // An all-items call is atomic from the caller's perspective: do not turn a
                // resource-limit rejection into a partial response by skipping one item.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Forecast failed for item {ItemId}, skipping", item.Id);
            }
        }

        return results;
    }

    private static void RejectWhenOverLimit(int observed, int maximum, string resource)
    {
        if (observed > maximum)
        {
            throw new ForecastResourceLimitExceededException(resource, maximum, maximum + 1);
        }
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
