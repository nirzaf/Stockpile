using Merconiq.Core.Models;

namespace Merconiq.Core.Interfaces;

/// <summary>Service contract for stock movement anomaly detection.</summary>
public interface IAnomalyDetectionService
{
    /// <summary>Detects unusual stock movements (spikes and drops) within an optional date range.</summary>
    /// <param name="from">Inclusive start date, or <see langword="null"/> for no lower bound.</param>
    /// <param name="to">Inclusive end date, or <see langword="null"/> for no upper bound.</param>
    /// <returns>A read-only list of detected anomalies.</returns>
    Task<IReadOnlyList<StockAnomaly>> DetectAnomaliesAsync(DateTime? from = null, DateTime? to = null);

    /// <summary>Detects anomalies using only movements owned by the specified companies.</summary>
    Task<IReadOnlyList<StockAnomaly>> DetectAnomaliesForCompaniesAsync(
        DateTime? from,
        DateTime? to,
        IReadOnlyCollection<int> companyIds);
}
