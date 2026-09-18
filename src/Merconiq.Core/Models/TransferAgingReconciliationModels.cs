using Merconiq.Core.Entities;

namespace Merconiq.Core.Models;

/// <summary>A bounded, read-only page of transfer-order line aging and persisted conservation evidence.</summary>
public sealed record TransferAgingReconciliationPage(
    DateTimeOffset AsOf,
    IReadOnlyList<TransferAgingReconciliationLine> Lines,
    int? NextAfterLineId);

/// <summary>Transfer-order line quantities and values reconciled to immutable transit and stock-ledger rows.</summary>
public sealed record TransferAgingReconciliationLine(
    int TransferOrderId,
    Guid TransferOrderDocumentId,
    TransferOrderStatus TransferOrderStatus,
    int CompanyId,
    int FromLocationId,
    int ToLocationId,
    int TransferOrderLineId,
    Guid SourceDocumentLineId,
    int ItemId,
    int OrderedQuantity,
    int ReservedQuantity,
    int DispatchedQuantity,
    int DispatchedLineCounterVariance,
    int ReceivedQuantity,
    int QuarantinedQuantity,
    int ReturnedQuantity,
    int OutstandingTransitQuantity,
    int QuantityConservationVariance,
    decimal? OriginalAverageUnitCost,
    decimal? OutstandingAverageUnitCost,
    decimal DispatchedValue,
    decimal ReceivedValue,
    decimal QuarantinedValue,
    decimal ReturnedValue,
    decimal OutstandingTransitValue,
    decimal ValueConservationVariance,
    int DispatchLedgerQuantityVariance,
    decimal DispatchLedgerValueVariance,
    int DispatchValuationPostingCountVariance,
    int DispatchValuationQuantityVariance,
    int SettlementLedgerQuantityVariance,
    decimal SettlementLedgerValueVariance,
    int SettlementValuationPostingCountVariance,
    int SettlementValuationQuantityVariance,
    DateTimeOffset? OldestOutstandingDispatchedAt,
    int? OldestOutstandingAgeDays,
    string? LatestTransitAction,
    string? LatestTransitActionBy,
    DateTimeOffset? LatestTransitActionAt);
