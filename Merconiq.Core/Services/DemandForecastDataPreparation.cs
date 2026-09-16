using Merconiq.Core.Entities;
using Merconiq.Core.Models;

namespace Merconiq.Core.Services;

/// <summary>Builds a complete calendar time series from customer sales.</summary>
public static class DemandForecastDataPreparation
{
    public static IReadOnlyList<DailyDemandObservation> BuildDailyDemand(
        IEnumerable<StockTransaction> transactions)
    {
        var salesByDay = transactions
            .Where(transaction => transaction.TransactionType == TransactionType.Sell)
            .GroupBy(transaction => transaction.TransactionDate.Date)
            .ToDictionary(
                group => group.Key,
                group => (float)group.Sum(transaction => transaction.Quantity));

        if (salesByDay.Count == 0)
        {
            return [];
        }

        var firstDay = salesByDay.Keys.Min();
        var lastDay = salesByDay.Keys.Max();
        var dayCount = checked((lastDay - firstDay).Days + 1);

        return Enumerable.Range(0, dayCount)
            .Select(offset =>
            {
                var date = firstDay.AddDays(offset);
                return new DailyDemandObservation(date, salesByDay.GetValueOrDefault(date));
            })
            .ToArray();
    }
}
