using System.Diagnostics.Metrics;
using Merconiq.Core;

namespace Merconiq.Core.Diagnostics;

/// <summary>Application metrics emitted by inventory workflows.</summary>
public static class InventoryTelemetry
{
    public const string MeterName = ProductIdentity.InventoryMeterName;
    public static readonly Meter Meter = new(MeterName, "1.0.0");
    public static readonly Counter<long> ConcurrencyRetries = Meter.CreateCounter<long>("merconiq.concurrency.retries");
    public static readonly Counter<long> ForecastFallbacks = Meter.CreateCounter<long>("merconiq.forecast.fallbacks");
    public static readonly Counter<long> LowStockAlerts = Meter.CreateCounter<long>("merconiq.low_stock.alerts");
    public static readonly Counter<long> WebhookFailures = Meter.CreateCounter<long>("merconiq.webhook.failures");
}
