using InventoryManagementSystem.Core.Models;

namespace InventoryManagementSystem.Core.Interfaces;

/// <summary>Service contract for per-item demand forecasting using ML.NET.</summary>
public interface IDemandForecastService
{
    /// <summary>Forecasts demand for a single item over the given horizon.</summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="horizonDays">The forecast horizon in days. Defaults to 30.</param>
    /// <returns>The forecast result, including historical context and predicted values.</returns>
    Task<DemandForecastResult> ForecastDemandAsync(int itemId, int horizonDays = 30);

    /// <summary>Forecasts demand for every item with sufficient transaction history.</summary>
    /// <param name="horizonDays">The forecast horizon in days. Defaults to 30.</param>
    /// <returns>A read-only list of per-item forecast results.</returns>
    Task<IReadOnlyList<DemandForecastResult>> ForecastAllItemsAsync(int horizonDays = 30);
}
