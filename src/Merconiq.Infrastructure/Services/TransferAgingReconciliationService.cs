using System.Data;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Infrastructure.Services;

/// <summary>Builds bounded transfer reconciliation rows from tenant-filtered persisted records.</summary>
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
        var lineReferences = selected.Select(row => row.Line.ReservationSourceLineReference).Distinct().ToArray();
        var orderIds = selected.Select(row => row.Order.Id).Distinct().ToArray();

        var reservations = await db.StockReservations.AsNoTracking()
            .Where(reservation => lineReferences.Contains(reservation.SourceLineReference) &&
                                  reservation.Status == StockReservationStatus.Active &&
                                  reservation.ExpiresAt > asOf)
            .Select(reservation => new ReservationSlice(
                reservation.SourceLineReference,
                reservation.ItemId,
                reservation.LocationId,
                reservation.Quantity - reservation.ConsumedQuantity))
            .ToListAsync(cancellationToken);

        var transitEntries = await db.TransferTransitEntries.AsNoTracking()
            .Where(entry => orderIds.Contains(entry.TransferOrderId) && lineIds.Contains(entry.TransferOrderLineId))
            .OrderBy(entry => entry.DispatchedAt)
            .ThenBy(entry => entry.Id)
            .ToListAsync(cancellationToken);

        var settlements = await db.TransferTransitSettlements.AsNoTracking()
            .Where(settlement => orderIds.Contains(settlement.TransferOrderId) &&
                                 lineIds.Contains(settlement.TransferOrderLineId))
            .OrderBy(settlement => settlement.SettledAt)
            .ThenBy(settlement => settlement.Id)
            .ToListAsync(cancellationToken);

        var stockTransactionIds = transitEntries.Select(entry => entry.StockTransactionId)
            .Concat(settlements.Select(settlement => settlement.StockTransactionId))
            .Distinct()
            .ToArray();
        List<StockTransaction> stockTransactions = stockTransactionIds.Length == 0
            ? []
            : await db.StockTransactions.AsNoTracking()
                .Where(transaction => stockTransactionIds.Contains(transaction.Id))
                .ToListAsync(cancellationToken);
        List<StockValuationEntry> valuationEntries = stockTransactionIds.Length == 0
            ? []
            : await db.StockValuationEntries.AsNoTracking()
                .Where(entry => stockTransactionIds.Contains(entry.StockTransactionId))
                .ToListAsync(cancellationToken);

        var reservationsByReference = reservations
            .GroupBy(reservation => reservation.SourceLineReference)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<ReservationSlice>)group.ToArray());
        var transitEntriesByLine = transitEntries
            .GroupBy(entry => entry.TransferOrderLineId)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<TransferTransitEntry>)group.ToArray());
        var settlementsByLine = settlements
            .GroupBy(settlement => settlement.TransferOrderLineId)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<TransferTransitSettlement>)group.ToArray());
        var transactionsById = stockTransactions.ToDictionary(transaction => transaction.Id);
        var valuationByTransactionId = valuationEntries
            .GroupBy(entry => entry.StockTransactionId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var rows = selected.Select(row => BuildLine(
                row.Line,
                row.Order,
                reservationsByReference.GetValueOrDefault(row.Line.ReservationSourceLineReference) ?? [],
                transitEntriesByLine.GetValueOrDefault(row.Line.Id) ?? [],
                settlementsByLine.GetValueOrDefault(row.Line.Id) ?? [],
                transactionsById,
                valuationByTransactionId,
                asOf))
            .ToArray();

        await transaction.CommitAsync(cancellationToken);
        return new TransferAgingReconciliationPage(
            asOf,
            rows,
            hasMore ? selected[^1].Line.Id : null);
    }

    private static TransferAgingReconciliationLine BuildLine(
        TransferOrderLine line,
        TransferOrder order,
        IReadOnlyCollection<ReservationSlice> reservations,
        IReadOnlyCollection<TransferTransitEntry> transitEntries,
        IReadOnlyCollection<TransferTransitSettlement> settlements,
        IReadOnlyDictionary<int, StockTransaction> transactionsById,
        IReadOnlyDictionary<int, StockValuationEntry[]> valuationByTransactionId,
        DateTimeOffset asOf)
    {
        var lineReservations = reservations.Where(reservation =>
                reservation.SourceLineReference == line.ReservationSourceLineReference &&
                reservation.ItemId == line.ItemId &&
                reservation.LocationId == order.FromLocationId)
            .Sum(reservation => Math.Max(0, reservation.Remaining));

        var settlementsByEntry = settlements
            .GroupBy(settlement => settlement.TransferTransitEntryId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var outstandingByEntry = transitEntries.Select(entry =>
        {
            var entrySettlements = settlementsByEntry.TryGetValue(entry.Id, out var recorded)
                ? recorded
                : [];
            return new
            {
                Entry = entry,
                Quantity = entry.Quantity - entrySettlements.Sum(settlement => settlement.Quantity),
                Value = entry.TotalValue - entrySettlements.Sum(settlement => settlement.TotalValue)
            };
        }).ToArray();

        var dispatchedQuantity = transitEntries.Sum(entry => entry.Quantity);
        var receivedQuantity = settlements
            .Where(settlement => settlement.SettlementType == TransferTransitSettlementType.Received)
            .Sum(settlement => settlement.Quantity);
        var quarantinedQuantity = settlements
            .Where(settlement => settlement.SettlementType == TransferTransitSettlementType.Quarantined)
            .Sum(settlement => settlement.Quantity);
        var returnedQuantity = settlements
            .Where(settlement => settlement.SettlementType == TransferTransitSettlementType.Returned)
            .Sum(settlement => settlement.Quantity);
        var outstandingQuantity = outstandingByEntry.Sum(entry => entry.Quantity);

        var dispatchedValue = transitEntries.Sum(entry => entry.TotalValue);
        var receivedValue = settlements
            .Where(settlement => settlement.SettlementType == TransferTransitSettlementType.Received)
            .Sum(settlement => settlement.TotalValue);
        var quarantinedValue = settlements
            .Where(settlement => settlement.SettlementType == TransferTransitSettlementType.Quarantined)
            .Sum(settlement => settlement.TotalValue);
        var returnedValue = settlements
            .Where(settlement => settlement.SettlementType == TransferTransitSettlementType.Returned)
            .Sum(settlement => settlement.TotalValue);
        var outstandingValue = outstandingByEntry.Sum(entry => entry.Value);
        var settlementQuantity = settlements.Sum(settlement => settlement.Quantity);
        var settlementValue = settlements.Sum(settlement => settlement.TotalValue);

        var dispatchLedgerQuantity = transitEntries.Sum(entry =>
            transactionsById.TryGetValue(entry.StockTransactionId, out var transaction) &&
            transaction.TransactionType == TransactionType.TransferDispatch &&
            transaction.ItemId == entry.ItemId &&
            transaction.FromLocationId == entry.FromLocationId &&
            transaction.ToLocationId == entry.ToLocationId
                ? transaction.Quantity
                : 0);
        var settlementLedgerQuantity = settlements.Sum(settlement =>
        {
            var isReturn = settlement.SettlementType == TransferTransitSettlementType.Returned;
            return transactionsById.TryGetValue(settlement.StockTransactionId, out var transaction) &&
                   transaction.TransactionType == (isReturn ? TransactionType.TransferReturn : TransactionType.TransferReceipt) &&
                   transaction.ItemId == settlement.ItemId &&
                   transaction.FromLocationId == (isReturn ? settlement.ToLocationId : settlement.FromLocationId) &&
                   transaction.ToLocationId == (isReturn ? settlement.FromLocationId : settlement.ToLocationId)
                ? transaction.Quantity
                : 0;
        });
        var dispatchLedgerValue = transitEntries.Sum(entry =>
            valuationByTransactionId.TryGetValue(entry.StockTransactionId, out var postings)
                ? postings.Where(posting => posting.EntryType == StockValuationEntryType.TransferOut &&
                                           posting.ItemId == entry.ItemId &&
                                           posting.LocationId == entry.FromLocationId)
                    .Sum(posting => posting.TotalValue)
                : 0m);
        var settlementLedgerValue = settlements.Sum(settlement =>
        {
            var isReturn = settlement.SettlementType == TransferTransitSettlementType.Returned;
            return valuationByTransactionId.TryGetValue(settlement.StockTransactionId, out var postings)
                ? postings.Where(posting =>
                        posting.EntryType == (isReturn
                            ? StockValuationEntryType.TransferReturn
                            : StockValuationEntryType.TransferIn) &&
                        posting.ItemId == settlement.ItemId &&
                        posting.LocationId == (isReturn ? settlement.FromLocationId : settlement.ToLocationId))
                    .Sum(posting => posting.TotalValue)
                : 0m;
        });

        var oldestOutstanding = outstandingByEntry
            .Where(entry => entry.Quantity > 0)
            .OrderBy(entry => entry.Entry.DispatchedAt)
            .ThenBy(entry => entry.Entry.Id)
            .FirstOrDefault();
        var latestAction = transitEntries
            .Select(entry => new TransitAction(entry.DispatchedAt, 0, entry.Id, "Dispatched", entry.DispatchedBy))
            .Concat(settlements.Select(settlement => new TransitAction(
                settlement.SettledAt,
                1,
                settlement.Id,
                settlement.SettlementType.ToString(),
                settlement.SettledBy)))
            .OrderByDescending(action => action.At)
            .ThenByDescending(action => action.Priority)
            .ThenByDescending(action => action.Id)
            .FirstOrDefault();

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
            lineReservations,
            dispatchedQuantity,
            line.DispatchedQuantity - dispatchedQuantity,
            receivedQuantity,
            quarantinedQuantity,
            returnedQuantity,
            outstandingQuantity,
            dispatchedQuantity - receivedQuantity - quarantinedQuantity - returnedQuantity - outstandingQuantity,
            dispatchedQuantity == 0 ? null : RoundCost(dispatchedValue / dispatchedQuantity),
            outstandingQuantity <= 0 ? null : RoundCost(outstandingValue / outstandingQuantity),
            dispatchedValue,
            receivedValue,
            quarantinedValue,
            returnedValue,
            outstandingValue,
            dispatchedValue - receivedValue - quarantinedValue - returnedValue - outstandingValue,
            dispatchedQuantity - dispatchLedgerQuantity,
            dispatchedValue - dispatchLedgerValue,
            settlementQuantity - settlementLedgerQuantity,
            settlementValue - settlementLedgerValue,
            oldestOutstanding?.Entry.DispatchedAt,
            oldestOutstanding is null
                ? null
                : Math.Max(0, (int)(asOf - oldestOutstanding.Entry.DispatchedAt).TotalDays),
            latestAction?.Name,
            latestAction?.Actor,
            latestAction?.At);
    }

    private sealed record TransitAction(
        DateTimeOffset At,
        int Priority,
        int Id,
        string Name,
        string Actor);

    private sealed record ReservationSlice(
        string SourceLineReference,
        int ItemId,
        int LocationId,
        int Remaining);

    private static decimal RoundCost(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);
}
