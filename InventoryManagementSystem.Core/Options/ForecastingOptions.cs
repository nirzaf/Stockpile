namespace InventoryManagementSystem.Core.Options;

/// <summary>Supported deployment choices for demand forecasting.</summary>
public static class ForecastingImplementations
{
    /// <summary>A platform-independent model that does not load native ML.NET libraries.</summary>
    public const string ManagedMovingAverage = "managed-moving-average";

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

    /// <summary>
    /// The model used by the application. Production defaults to the managed
    /// implementation so the amd64 and arm64 images have identical behavior.
    /// </summary>
    public string Implementation { get; set; } = ForecastingImplementations.ManagedMovingAverage;
}
