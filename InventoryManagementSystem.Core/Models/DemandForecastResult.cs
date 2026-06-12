namespace InventoryManagementSystem.Core.Models;

/// <summary>Result of a per-item demand forecast run.</summary>
public class DemandForecastResult
{
    /// <summary>Identifier of the item being forecast.</summary>
    public int ItemId { get; set; }

    /// <summary>Display name of the item (item code or fallback).</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Predicted demand for each day in the forecast horizon.</summary>
    public List<float> ForecastedValues { get; set; } = [];

    /// <summary>Lower bound of the prediction confidence interval per day.</summary>
    public List<float> ConfidenceLower { get; set; } = [];

    /// <summary>Upper bound of the prediction confidence interval per day.</summary>
    public List<float> ConfidenceUpper { get; set; } = [];

    /// <summary>Mean of the historical demand values used to train the model.</summary>
    public float AverageDailyDemand { get; set; }

    /// <summary>Number of days of historical data used.</summary>
    public int TotalHistoricalDays { get; set; }

    /// <summary>Length of the forecast horizon in days.</summary>
    public int ForecastHorizonDays { get; set; }

    /// <summary>UTC timestamp of when the forecast was generated.</summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
