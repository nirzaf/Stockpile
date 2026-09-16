namespace Merconiq.Core.Options;

/// <summary>Supported deployment choices for demand forecasting.</summary>
public static class ForecastingImplementations
{
    /// <summary>A platform-independent model that does not load native ML.NET libraries.</summary>
    public const string ManagedMovingAverage = "managed-moving-average";

    /// <summary>Version of the managed moving-average calculation and response contract.</summary>
    public const string ManagedMovingAverageVersion = "1.0.0";

    /// <summary>ML.NET SSA, which requires its native runtime dependencies.</summary>
    public const string Ssa = "ssa";

    /// <summary>Returns whether the implementation name is supported.</summary>
    public static bool IsSupported(string? implementation) =>
        string.Equals(implementation, ManagedMovingAverage, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(implementation, Ssa, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Configuration for the demand forecasting runtime.</summary>
public sealed class ForecastingOptions
{
    public const string SectionName = "Forecasting";

    public const int MinimumHistoricalDays = 5;
    public const int DefaultMaxForecastHorizonDays = 90;
    public const int DefaultMaxHistoricalDays = 365;
    public const int AbsoluteMaxForecastHorizonDays = 365;
    public const int AbsoluteMaxHistoricalDays = 3650;

    /// <summary>
    /// The model used by the application. Production defaults to the managed
    /// implementation so the amd64 and arm64 images have identical behavior.
    /// </summary>
    public string Implementation { get; set; } = ForecastingImplementations.ManagedMovingAverage;

    /// <summary>Maximum number of daily values returned by a forecast request.</summary>
    public int MaxForecastHorizonDays { get; set; } = DefaultMaxForecastHorizonDays;

    /// <summary>Maximum inclusive UTC calendar-day window considered for historical sales.</summary>
    public int MaxHistoricalDays { get; set; } = DefaultMaxHistoricalDays;

    /// <summary>Returns whether configured forecast limits are within the supported hard bounds.</summary>
    public static bool HasValidResourceLimits(ForecastingOptions options) =>
        options.MaxForecastHorizonDays is >= 1 and <= AbsoluteMaxForecastHorizonDays &&
        options.MaxHistoricalDays is >= MinimumHistoricalDays and <= AbsoluteMaxHistoricalDays;
}
