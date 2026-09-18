using System.ComponentModel.DataAnnotations;
using Merconiq.Core.Entities;

namespace Merconiq.Core.Models;

public sealed record StockCountReconciliationRequest(
    [property: Range(1, int.MaxValue)] int LocationId,
    [property: Range(1, int.MaxValue)] int? CompanyId = null,
    [property: Range(1, int.MaxValue)] int? ItemId = null,
    [param: StringLength(100)] string? BatchNumber = null,
    DateTime? ExpiryDate = null);

public sealed record StockCountReconciliationMovementView(
    int StockTransactionId,
    DateTime TransactionDate,
    TransactionType TransactionType,
    int QuantityDelta,
    decimal? UnitCost,
    decimal? ValueDelta,
    string? SourceLineReference,
    string? Notes);

public sealed record StockCountReconciliationPositionView(
    int ItemId,
    string ItemCode,
    string ItemDescription,
    string? BatchNumber,
    DateTime? ExpiryDate,
    int OnHandQuantity,
    int ReservedQuantity,
    int QuarantinedQuantity,
    int AvailableQuantity,
    int LedgerQuantity,
    int QuantityDifference,
    bool ValuationTracked,
    int? ValuationBucketQuantity,
    decimal? ValuationBucketValue,
    int? ValuationLedgerQuantity,
    decimal? ValuationLedgerValue,
    int? ValuationQuantityDifference,
    int? ValuationLedgerQuantityDifference,
    decimal? ValuationValueDifference,
    IReadOnlyList<StockCountReconciliationMovementView> Ledger);

public sealed record StockCountReconciliationView(
    int LocationId,
    int? CompanyId,
    DateTime GeneratedAtUtc,
    IReadOnlyList<StockCountReconciliationPositionView> Positions);
