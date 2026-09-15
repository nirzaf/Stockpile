using System.Diagnostics.Metrics;

namespace InventoryManagementSystem.Core.Diagnostics;

/// <summary>Application metrics emitted by inventory workflows.</summary>
public static class InventoryTelemetry
{
    public const string MeterName = "Stockpile.Inventory";
    public static readonly Meter Meter = new(MeterName, "1.0.0");
    public static readonly Counter<long> ConcurrencyRetries = Meter.CreateCounter<long>("stockpile.concurrency.retries");
    public static readonly Counter<long> ForecastFallbacks = Meter.CreateCounter<long>("stockpile.forecast.fallbacks");
    public static readonly Counter<long> LowStockAlerts = Meter.CreateCounter<long>("stockpile.low_stock.alerts");
    public static readonly Counter<long> WebhookFailures = Meter.CreateCounter<long>("stockpile.webhook.failures");
}
