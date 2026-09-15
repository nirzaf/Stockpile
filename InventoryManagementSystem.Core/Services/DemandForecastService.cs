using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.ML;
using Microsoft.ML.Transforms.TimeSeries;

namespace InventoryManagementSystem.Core.Services;

/// <summary>
/// Demand forecast service. Uses ML.NET's SSA (Singular Spectrum Analysis) time-series
/// forecaster per item, falling back to a moving average if the ML model fails. Results
/// are cached in memory until the process restarts.
/// </summary>
public class DemandForecastService : IDemandForecastService
{
    private readonly IRepository<StockTransaction> _txRepo;
    private readonly IRepository<Item> _itemRepo;
    private readonly ILogger<DemandForecastService> _logger;
    private readonly IMemoryCache _cache;

    private const int MinDataPoints = 5;
    private const int DefaultWindowSize = 7;
    private const float ConfidenceLevel = 0.95f;

    public DemandForecastService(
        IRepository<StockTransaction> txRepo,
        IRepository<Item> itemRepo,
        ILogger<DemandForecastService> logger,
        IMemoryCache cache)
    {
        _txRepo = txRepo;
        _itemRepo = itemRepo;
        _logger = logger;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<DemandForecastResult> ForecastDemandAsync(int itemId, int horizonDays = 30)
    {
        if (_cache.TryGetValue($"forecast_{itemId}", out DemandForecastResult? cachedResult) && cachedResult != null)
        {
            _logger.LogDebug("Returning cached forecast for item {ItemId}", itemId);
            return cachedResult;
        }

        return await GenerateForecastAsync(itemId, horizonDays);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DemandForecastResult>> ForecastAllItemsAsync(int horizonDays = 30)
    {
        if (_cache.TryGetValue("forecast_all", out IReadOnlyList<DemandForecastResult>? cachedResult) && cachedResult != null)
        {
            _logger.LogDebug("Returning cached forecasts for all items");
            return cachedResult;
        }

        return await GenerateForecastAllItemsAsync(horizonDays);
    }

    private async Task<DemandForecastResult> GenerateForecastAsync(int itemId, int horizonDays = 30)
    {
        var item = (await _itemRepo.FindAsync(i => i.Id == itemId)).FirstOrDefault();

        var result = new DemandForecastResult
        {
            ItemId = itemId,
            ItemName = item?.ItemCode ?? $"Item #{itemId}",
            ForecastHorizonDays = horizonDays
        };

        var transactions = await _txRepo.FindAsync(t =>
            t.ItemId == itemId &&
            (t.TransactionType == TransactionType.Sell || t.TransactionType == TransactionType.Transfer));

        var dailyDemand = transactions
            .GroupBy(t => t.TransactionDate.Date)
            .Select(g => new { Date = g.Key, Quantity = (float)g.Sum(t => t.Quantity) })
            .OrderBy(x => x.Date)
            .ToList();

        if (dailyDemand.Count < MinDataPoints)
        {
            _logger.LogWarning("Insufficient data for item {ItemId}: {Count} days (need {Min})",
                itemId, dailyDemand.Count, MinDataPoints);
            return result;
        }

        result.TotalHistoricalDays = dailyDemand.Count;
        result.AverageDailyDemand = dailyDemand.Average(d => d.Quantity);

        try
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
            // ML.NET can fail on degenerate inputs (e.g. all-zero series, NaN, constant
            // values). Falling back to a flat moving-average keeps the API contract intact
            // and avoids surfacing a 500 to the caller for a non-essential insight.
            _logger.LogError(ex, "ML forecast failed for item {ItemId}, falling back to moving average", itemId);
            result.ForecastedValues = Enumerable.Repeat(result.AverageDailyDemand, horizonDays).ToList();
        }

        return result;
    }

    private async Task<IReadOnlyList<DemandForecastResult>> GenerateForecastAllItemsAsync(int horizonDays = 30)
    {
        var items = await _itemRepo.GetAllAsync();
        
        var forecastTasks = items.Select(async item =>
        {
            try
            {
                return await GenerateForecastAsync(item.Id, horizonDays);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Forecast failed for item {ItemId}, skipping", item.Id);
                return null;
            }
        });

        var forecasts = await Task.WhenAll(forecastTasks);
        var results = forecasts
            .Where(f => f is not null)
            .Where(f => f!.ForecastedValues.Count > 0)
            .Select(f => f!)
            .ToList();

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
