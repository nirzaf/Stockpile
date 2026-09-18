using System.Data;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Infrastructure.Services;

/// <summary>Builds bounded transfer reconciliation pages from tenant-filtered database aggregates.</summary>
public sealed class TransferAgingReconciliationService(InventoryDbContext db)
    : ITransferAgingReconciliationService
{
    public async Task<TransferAgingReconciliationPage> GetPageAsync(
        IReadOnlyCollection<int> authorizedCompanyIds,
        int? afterLineId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizedCompanyIds);
        if (pageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 100.");
        if (afterLineId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(afterLineId));

        var companyIds = authorizedCompanyIds.Where(id => id > 0).Distinct().ToArray();
        if (companyIds.Length == 0)
            return new TransferAgingReconciliationPage(DateTimeOffset.UtcNow, [], null);

        var tenantId = db.CurrentTenantId;
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        var asOf = DateTimeOffset.UtcNow;
        var selected = await (
                from line in db.TransferOrderLines.AsNoTracking()
                join order in db.TransferOrders.AsNoTracking()
                    on new { TransferOrderId = line.TransferOrderId, line.TenantId }
                    equals new { TransferOrderId = order.Id, order.TenantId }
                where line.TenantId == tenantId &&
                      order.TenantId == tenantId &&
                      companyIds.Contains(order.CompanyId) &&
                      (!afterLineId.HasValue || line.Id > afterLineId.Value)
                orderby line.Id
                select new { Line = line, Order = order })
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = selected.Count > pageSize;
        if (hasMore)
            selected.RemoveAt(selected.Count - 1);
        if (selected.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return new TransferAgingReconciliationPage(asOf, [], null);
        }

        var lineIds = selected.Select(row => row.Line.Id).ToArray();
        var orderIds = selected.Select(row => row.Order.Id).Distinct().ToArray();
        var dispatchEntries = db.TransferTransitEntries.AsNoTracking()
            .Where(entry => entry.TenantId == tenantId &&
                            orderIds.Contains(entry.TransferOrderId) &&
                            lineIds.Contains(entry.TransferOrderLineId));
        var settlementEntries = db.TransferTransitSettlements.AsNoTracking()
            .Where(settlement => settlement.TenantId == tenantId &&
                                 orderIds.Contains(settlement.TransferOrderId) &&
                                 lineIds.Contains(settlement.TransferOrderLineId));

        var lineReferences = selected.Select(row => row.Line.ReservationSourceLineReference).Distinct().ToArray();
        var reservations = await db.StockReservations.AsNoTracking()
            .Where(reservation => reservation.TenantId == tenantId &&
                                  lineReferences.Contains(reservation.SourceLineReference) &&
                                  reservation.Status == StockReservationStatus.Active &&
                                  reservation.ExpiresAt > asOf)
            .GroupBy(reservation => new
            {
                reservation.SourceLineReference,
                reservation.ItemId,
                reservation.LocationId
            })
            .Select(group => new ReservationTotal(
                group.Key.SourceLineReference,
                group.Key.ItemId,
                group.Key.LocationId,
                group.Sum(reservation => reservation.Quantity - reservation.ConsumedQuantity)))
            .ToListAsync(cancellationToken);

        var dispatchTotals = await dispatchEntries
            .GroupBy(entry => entry.TransferOrderLineId)
            .Select(group => new DispatchTotal(
                group.Key,
                group.Count(),
                group.Sum(entry => entry.Quantity),
                group.Sum(entry => entry.TotalValue)))
            .ToListAsync(cancellationToken);

        var settlementTotals = await settlementEntries
            .GroupBy(settlement => settlement.TransferOrderLineId)
            .Select(group => new SettlementTotal(
                group.Key,
                group.Count(),
                group.Sum(settlement => settlement.SettlementType == TransferTransitSettlementType.Received
                    ? settlement.Quantity
                    : 0),
                group.Sum(settlement => settlement.SettlementType == TransferTransitSettlementType.Quarantined
                    ? settlement.Quantity
                    : 0),
                group.Sum(settlement => settlement.SettlementType == TransferTransitSettlementType.Returned
                    ? settlement.Quantity
                    : 0),
                group.Sum(settlement => settlement.Quantity),
                group.Sum(settlement => settlement.SettlementType == TransferTransitSettlementType.Received
                    ? settlement.TotalValue
                    : 0m),
                group.Sum(settlement => settlement.SettlementType == TransferTransitSettlementType.Quarantined
                    ? settlement.TotalValue
                    : 0m),
                group.Sum(settlement => settlement.SettlementType == TransferTransitSettlementType.Returned
                    ? settlement.TotalValue
                    : 0m),
                group.Sum(settlement => settlement.TotalValue)))
            .ToListAsync(cancellationToken);

        var dispatchTransactionVariances = await (
                from entry in dispatchEntries
                join stock in db.StockTransactions.AsNoTracking()
                    on new { entry.StockTransactionId, entry.TenantId }
                    equals new { StockTransactionId = stock.Id, stock.TenantId }
                group new { Entry = entry, Stock = stock } by entry.TransferOrderLineId into g
                select new QuantityVariance(
                    g.Key,
                    g.Sum(row => Math.Abs(row.Entry.Quantity -
                        (row.Stock.TransactionType == TransactionType.TransferDispatch &&
                         row.Stock.ItemId == row.Entry.ItemId &&
                         row.Stock.FromLocationId == row.Entry.FromLocationId &&
                         row.Stock.ToLocationId == row.Entry.ToLocationId
                            ? row.Stock.Quantity
                            : 0)))))
            .ToListAsync(cancellationToken);

        var settlementTransactionVariances = await (
                from settlement in settlementEntries
                join stock in db.StockTransactions.AsNoTracking()
                    on new { settlement.StockTransactionId, settlement.TenantId }
                    equals new { StockTransactionId = stock.Id, stock.TenantId }
                group new { Settlement = settlement, Stock = stock } by settlement.TransferOrderLineId into g
                select new QuantityVariance(
                    g.Key,
                    g.Sum(row => Math.Abs(row.Settlement.Quantity -
                        (row.Stock.TransactionType == (row.Settlement.SettlementType ==
                                                       TransferTransitSettlementType.Returned
                                ? TransactionType.TransferReturn
                                : TransactionType.TransferReceipt) &&
                         row.Stock.ItemId == row.Settlement.ItemId &&
                         row.Stock.FromLocationId == (row.Settlement.SettlementType ==
                                                       TransferTransitSettlementType.Returned
                                ? row.Settlement.ToLocationId
                                : row.Settlement.FromLocationId) &&
                         row.Stock.ToLocationId == (row.Settlement.SettlementType ==
                                                     TransferTransitSettlementType.Returned
                                ? row.Settlement.FromLocationId
                                : row.Settlement.ToLocationId)
                            ? row.Stock.Quantity
                            : 0)))))
            .ToListAsync(cancellationToken);

        var eventTransactionIds = dispatchEntries.Select(entry => entry.StockTransactionId)
            .Concat(settlementEntries.Select(settlement => settlement.StockTransactionId));
        var valuationPostings = db.StockValuationEntries.AsNoTracking()
            .Where(posting => posting.TenantId == tenantId &&
                              eventTransactionIds.Contains(posting.StockTransactionId))
            .GroupBy(posting => new
            {
                posting.StockTransactionId,
                posting.EntryType,
                posting.ItemId,
                posting.LocationId
            })
            .Select(group => new
            {
                group.Key.StockTransactionId,
                group.Key.EntryType,
                group.Key.ItemId,
                group.Key.LocationId,
                Count = group.Count(),
                Quantity = group.Sum(posting => posting.Quantity),
                TotalValue = group.Sum(posting => posting.TotalValue)
            });

        var dispatchValuationVariances = await (
                from entry in dispatchEntries
                join posting in valuationPostings
                    on new
                    {
                        entry.StockTransactionId,
                        EntryType = StockValuationEntryType.TransferOut,
                        entry.ItemId,
                        LocationId = entry.FromLocationId
                    }
                    equals new
                    {
                        posting.StockTransactionId,
                        posting.EntryType,
                        posting.ItemId,
                        posting.LocationId
                    }
                    into matchingPostings
                from posting in matchingPostings.DefaultIfEmpty()
                group new
                {
                    entry.TransferOrderLineId,
                    ExpectedQuantity = entry.Quantity,
                    ExpectedValue = entry.TotalValue,
                    PostingCount = posting == null ? 0 : posting.Count,
                    PostingQuantity = posting == null ? 0 : posting.Quantity,
                    PostingValue = posting == null ? 0m : posting.TotalValue
                } by entry.TransferOrderLineId into g
                select new ValuationVariance(
                    g.Key,
                    g.Sum(row => Math.Abs(1 - row.PostingCount)),
                    g.Sum(row => Math.Abs(row.ExpectedQuantity - row.PostingQuantity)),
                    g.Sum(row => Math.Abs(row.ExpectedValue - row.PostingValue))))
            .ToListAsync(cancellationToken);

        var settlementValuationVariances = await (
                from settlement in settlementEntries
                join posting in valuationPostings
                    on new
                    {
                        settlement.StockTransactionId,
                        EntryType = settlement.SettlementType == TransferTransitSettlementType.Returned
                            ? StockValuationEntryType.TransferReturn
                            : StockValuationEntryType.TransferIn,
                        settlement.ItemId,
                        LocationId = settlement.SettlementType == TransferTransitSettlementType.Returned
                            ? settlement.FromLocationId
                            : settlement.ToLocationId
                    }
                    equals new
                    {
                        posting.StockTransactionId,
                        posting.EntryType,
                        posting.ItemId,
                        posting.LocationId
                    }
                    into matchingPostings
                from posting in matchingPostings.DefaultIfEmpty()
                group new
                {
                    settlement.TransferOrderLineId,
                    ExpectedQuantity = settlement.Quantity,
                    ExpectedValue = settlement.TotalValue,
                    PostingCount = posting == null ? 0 : posting.Count,
                    PostingQuantity = posting == null ? 0 : posting.Quantity,
                    PostingValue = posting == null ? 0m : posting.TotalValue
                } by settlement.TransferOrderLineId into g
                select new ValuationVariance(
                    g.Key,
                    g.Sum(row => Math.Abs(1 - row.PostingCount)),
                    g.Sum(row => Math.Abs(row.ExpectedQuantity - row.PostingQuantity)),
                    g.Sum(row => Math.Abs(row.ExpectedValue - row.PostingValue))))
            .ToListAsync(cancellationToken);

        var settlementByTransitEntry = settlementEntries
            .GroupBy(settlement => settlement.TransferTransitEntryId)
            .Select(group => new TransitSettlementTotal(
                group.Key,
                group.Sum(settlement => settlement.Quantity)));
        var oldestOutstanding = await (
                from entry in dispatchEntries
                join settlement in settlementByTransitEntry
                    on entry.Id equals settlement.TransferTransitEntryId into matchingSettlements
                from settlement in matchingSettlements.DefaultIfEmpty()
                where entry.Quantity > (settlement == null ? 0 : settlement.Quantity)
                group new { entry.Id, entry.DispatchedAt } by entry.TransferOrderLineId into g
                select g.OrderBy(entry => entry.DispatchedAt)
                    .ThenBy(entry => entry.Id)
                    .Select(entry => new OutstandingDispatch(g.Key, entry.Id, entry.DispatchedAt))
                    .First())
            .ToListAsync(cancellationToken);

        var dispatchActions = dispatchEntries.Select(entry => new TransitAction(
            entry.TransferOrderLineId,
            entry.DispatchedAt,
            0,
            entry.Id,
            "Dispatched",
            entry.DispatchedBy));
        var settlementActions = settlementEntries.Select(settlement => new TransitAction(
            settlement.TransferOrderLineId,
            settlement.SettledAt,
            1,
            settlement.Id,
            settlement.SettlementType == TransferTransitSettlementType.Received
                ? "Received"
                : settlement.SettlementType == TransferTransitSettlementType.Quarantined
                    ? "Quarantined"
                    : "Returned",
            settlement.SettledBy));
        var latestActions = await dispatchActions.Concat(settlementActions)
            .GroupBy(action => action.TransferOrderLineId)
            .Select(group => group.OrderByDescending(action => action.At)
                .ThenByDescending(action => action.Priority)
                .ThenByDescending(action => action.Id)
                .First())
            .ToListAsync(cancellationToken);

        var reservationByKey = reservations.ToDictionary(
            reservation => (reservation.SourceLineReference, reservation.ItemId, reservation.LocationId),
            reservation => reservation.Quantity);
        var dispatchByLine = dispatchTotals.ToDictionary(total => total.TransferOrderLineId);
        var settlementByLine = settlementTotals.ToDictionary(total => total.TransferOrderLineId);
        var dispatchTransactionByLine = dispatchTransactionVariances.ToDictionary(total => total.TransferOrderLineId);
        var settlementTransactionByLine = settlementTransactionVariances.ToDictionary(total => total.TransferOrderLineId);
        var dispatchValuationByLine = dispatchValuationVariances.ToDictionary(total => total.TransferOrderLineId);
        var settlementValuationByLine = settlementValuationVariances.ToDictionary(total => total.TransferOrderLineId);
        var oldestOutstandingByLine = oldestOutstanding.ToDictionary(entry => entry.TransferOrderLineId);
        var latestActionByLine = latestActions.ToDictionary(action => action.TransferOrderLineId);

        var rows = selected.Select(row =>
        {
            var line = row.Line;
            var order = row.Order;
            var lineId = line.Id;
            var dispatched = dispatchByLine.GetValueOrDefault(lineId) ?? DispatchTotal.Empty(lineId);
            var settled = settlementByLine.GetValueOrDefault(lineId) ?? SettlementTotal.Empty(lineId);
            var dispatchTransaction = dispatchTransactionByLine.GetValueOrDefault(lineId);
            var settlementTransaction = settlementTransactionByLine.GetValueOrDefault(lineId);
            var dispatchValuation = dispatchValuationByLine.GetValueOrDefault(lineId);
            var settlementValuation = settlementValuationByLine.GetValueOrDefault(lineId);
            var oldest = oldestOutstandingByLine.GetValueOrDefault(lineId);
            var latest = latestActionByLine.GetValueOrDefault(lineId);
            var outstandingQuantity = dispatched.Quantity - settled.Quantity;
            var outstandingValue = dispatched.Value - settled.Value;
            var reservedQuantity = reservationByKey.GetValueOrDefault(
                (line.ReservationSourceLineReference, line.ItemId, order.FromLocationId));

            return new TransferAgingReconciliationLine(
                order.Id,
                order.DocumentId.Value,
                order.Status,
                order.CompanyId,
                order.FromLocationId,
                order.ToLocationId,
                line.Id,
                line.DocumentLineId.Value,
                line.ItemId,
                line.Quantity,
                reservedQuantity,
                dispatched.Quantity,
                line.DispatchedQuantity - dispatched.Quantity,
                settled.ReceivedQuantity,
                settled.QuarantinedQuantity,
                settled.ReturnedQuantity,
                outstandingQuantity,
                dispatched.Quantity - settled.ReceivedQuantity - settled.QuarantinedQuantity -
                settled.ReturnedQuantity - outstandingQuantity,
                dispatched.Quantity == 0 ? null : RoundCost(dispatched.Value / dispatched.Quantity),
                outstandingQuantity <= 0 ? null : RoundCost(outstandingValue / outstandingQuantity),
                dispatched.Value,
                settled.ReceivedValue,
                settled.QuarantinedValue,
                settled.ReturnedValue,
                outstandingValue,
                dispatched.Value - settled.ReceivedValue - settled.QuarantinedValue -
                settled.ReturnedValue - outstandingValue,
                dispatchTransaction?.Difference ?? 0,
                dispatchValuation?.ValueVariance ?? 0m,
                dispatchValuation?.PostingCountVariance ?? 0,
                dispatchValuation?.QuantityVariance ?? 0,
                settlementTransaction?.Difference ?? 0,
                settlementValuation?.ValueVariance ?? 0m,
                settlementValuation?.PostingCountVariance ?? 0,
                settlementValuation?.QuantityVariance ?? 0,
                oldest?.DispatchedAt,
                oldest is null ? null : Math.Max(0, (int)(asOf - oldest.DispatchedAt).TotalDays),
                latest?.Name,
                latest?.Actor,
                latest?.At);
        }).ToArray();

        await transaction.CommitAsync(cancellationToken);
        return new TransferAgingReconciliationPage(
            asOf,
            rows,
            hasMore ? selected[^1].Line.Id : null);
    }

    private sealed record ReservationTotal(
        string SourceLineReference,
        int ItemId,
        int LocationId,
        int Quantity);

    private sealed record DispatchTotal(
        int TransferOrderLineId,
        int EventCount,
        int Quantity,
        decimal Value)
    {
        public static DispatchTotal Empty(int transferOrderLineId) => new(transferOrderLineId, 0, 0, 0m);
    }

    private sealed record SettlementTotal(
        int TransferOrderLineId,
        int EventCount,
        int ReceivedQuantity,
        int QuarantinedQuantity,
        int ReturnedQuantity,
        int Quantity,
        decimal ReceivedValue,
        decimal QuarantinedValue,
        decimal ReturnedValue,
        decimal Value)
    {
        public static SettlementTotal Empty(int transferOrderLineId) =>
            new(transferOrderLineId, 0, 0, 0, 0, 0, 0m, 0m, 0m, 0m);
    }

    private sealed record QuantityVariance(int TransferOrderLineId, int Difference);

    private sealed record ValuationVariance(
        int TransferOrderLineId,
        int PostingCountVariance,
        int QuantityVariance,
        decimal ValueVariance);

    private sealed record TransitSettlementTotal(int TransferTransitEntryId, int Quantity);

    private sealed record OutstandingDispatch(
        int TransferOrderLineId,
        int TransferTransitEntryId,
        DateTimeOffset DispatchedAt);

    private sealed record TransitAction(
        int TransferOrderLineId,
        DateTimeOffset At,
        int Priority,
        int Id,
        string Name,
        string Actor);

    private static decimal RoundCost(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);
}
