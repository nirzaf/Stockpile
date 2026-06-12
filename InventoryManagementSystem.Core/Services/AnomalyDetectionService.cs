using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Transforms.TimeSeries;

namespace InventoryManagementSystem.Core.Services;

/// <summary>
/// Anomaly detection service. Uses ML.NET's IID (Independent and Identically Distributed)
/// spike detector to flag days where stock movements deviate significantly from the historical
/// baseline.
/// </summary>
public class AnomalyDetectionService : IAnomalyDetectionService
{
    private readonly IRepository<StockTransaction> _txRepo;
    private readonly IRepository<Item> _itemRepo;
    private readonly ILogger<AnomalyDetectionService> _logger;

    private const int MinDataPoints = 8;
    private const double ConfidenceThreshold = 95.0;

    public AnomalyDetectionService(
        IRepository<StockTransaction> txRepo,
        IRepository<Item> itemRepo,
        ILogger<AnomalyDetectionService> logger)
    {
        _txRepo = txRepo;
        _itemRepo = itemRepo;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StockAnomaly>> DetectAnomaliesAsync(DateTime? from = null, DateTime? to = null)
    {
        var anomalies = new List<StockAnomaly>();

        // Push date filtering to the database — avoids loading entire transaction table
        var transactions = (await _txRepo.FindAsync(t =>
            (from == null || t.TransactionDate >= from.Value) &&
            (to == null || t.TransactionDate <= to.Value))).ToList();
        var items = (await _itemRepo.GetAllAsync()).ToDictionary(i => i.Id, i => i.ItemCode);

        // Group transactions by item
        var itemGroups = transactions
            .GroupBy(t => t.ItemId);

        foreach (var group in itemGroups)
        {
            var dailyData = group
                .GroupBy(t => t.TransactionDate.Date)
                .Select(g => new DailyTransactionData
                {
                    Date = g.Key,
                    Quantity = (float)g.Sum(t => t.Quantity)
                })
                .OrderBy(d => d.Date)
                .ToList();

            if (dailyData.Count < MinDataPoints) continue;

            try
            {
                var itemAnomalies = DetectSpikes(dailyData, group.Key,
                    items.GetValueOrDefault(group.Key, $"Item #{group.Key}"));
                anomalies.AddRange(itemAnomalies);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Anomaly detection skipped for item {ItemId}: {Message}",
                    group.Key, ex.Message);
            }
        }

        _logger.LogInformation("Anomaly detection complete: {Count} anomalies found across {Items} items",
            anomalies.Count, itemGroups.Count());

        return anomalies;
    }

    private List<StockAnomaly> DetectSpikes(List<DailyTransactionData> dailyData, int itemId, string itemName)
    {
        var mlContext = new MLContext(seed: 42);
        var values = dailyData.Select(d => d.Quantity).ToArray();

        var data = values.Select(v => new SpikeDataPoint { Quantity = v }).ToList();
        var dataView = mlContext.Data.LoadFromEnumerable(data);

        var pipeline = mlContext.Transforms.DetectIidSpike(
            outputColumnName: nameof(SpikePrediction.Prediction),
            inputColumnName: nameof(SpikeDataPoint.Quantity),
            confidence: ConfidenceThreshold,
            pvalueHistoryLength: values.Length / 2);

        var transformed = pipeline.Fit(dataView).Transform(dataView);
        var predictions = mlContext.Data
            .CreateEnumerable<SpikePrediction>(transformed, reuseRowObject: false)
            .ToList();

        var anomalies = new List<StockAnomaly>();

        for (int i = 0; i < predictions.Count; i++)
        {
            var pred = predictions[i];
            // Alert[0]=1 means spike, Alert[1]=1 means drop, Alert[2]=1 means both
            bool isSpike = pred.Prediction.Length > 0 && pred.Prediction[0] == 1;
            bool isDrop = pred.Prediction.Length > 1 && pred.Prediction[1] == 1;

            if (isSpike || isDrop)
            {
                anomalies.Add(new StockAnomaly
                {
                    ItemId = itemId,
                    ItemName = itemName,
                    Date = dailyData[i].Date,
                    ActualValue = dailyData[i].Quantity,
                    ExpectedValue = predictions.Take(i).Any()
                        ? predictions.Take(i).Average(p => (float)(p.Prediction.Length > 2 ? p.Prediction[2] : dailyData[i].Quantity))
                        : dailyData[i].Quantity,
                    ConfidenceScore = pred.Prediction.Length > 2 ? pred.Prediction[2] : 1.0,
                    AnomalyType = isSpike ? "Spike" : "Drop"
                });
            }
        }

        return anomalies;
    }

    // —— ML.NET data contracts ——

    private class DailyTransactionData
    {
        public DateTime Date { get; set; }
        public float Quantity { get; set; }
    }

    private class SpikeDataPoint
    {
        public float Quantity { get; set; }
    }

    private class SpikePrediction
    {
        public double[] Prediction { get; set; } = [];
    }
}
