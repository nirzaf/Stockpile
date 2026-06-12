namespace InventoryManagementSystem.Core.Models;

/// <summary>
/// Represents a single detected stock anomaly. <see cref="AnomalyType"/> is one of
/// <c>Spike</c> (unusually high movement) or <c>Drop</c> (unusually low movement).
/// </summary>
public class StockAnomaly
{
    /// <summary>Identifier of the item the anomaly relates to.</summary>
    public int ItemId { get; set; }

    /// <summary>Display name (item code) of the item.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>The day on which the anomaly was detected.</summary>
    public DateTime Date { get; set; }

    /// <summary>The observed quantity on the day of the anomaly.</summary>
    public float ActualValue { get; set; }

    /// <summary>The model-predicted expected quantity for the day.</summary>
    public float ExpectedValue { get; set; }

    /// <summary>Confidence score of the detection in the range 0-1.</summary>
    public double ConfidenceScore { get; set; }

    /// <summary>Either <c>Spike</c> or <c>Drop</c>.</summary>
    public string AnomalyType { get; set; } = string.Empty;
}
