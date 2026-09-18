using System.ComponentModel.DataAnnotations;

namespace Merconiq.Core.Models;

public sealed record StartStockCountRequest(
    [property: Range(1, int.MaxValue)] int LocationId);

public sealed record RecordStockCountObservationRequest(
    [property: Range(0, int.MaxValue)] int CountedQuantity);

public sealed record PostStockCountVarianceRequest(
    [property: Required, StringLength(500, MinimumLength = 1)] string Reason,
    decimal? ApprovedUnitCost = null);

public sealed record StockCountMovementRequest(
    int ItemId,
    int LocationId,
    int ExpectedCurrentQuantity,
    int CountedQuantity,
    string? BatchNumber,
    DateTime? ExpiryDate,
    string Reason,
    decimal? ApprovedUnitCost,
    string SourceLineReference);

public sealed record StockCountMovementResult(
    int StockTransactionId,
    decimal? UnitCost,
    decimal? SignedValueAdjustment);

public sealed record StockCountVarianceView(
    int ExpectedCurrentQuantity,
    int CountedQuantity,
    int DeltaQuantity,
    string Reason,
    decimal? ApprovedUnitCost,
    decimal? ValueAdjustment,
    int? StockTransactionId,
    DateTime PostedAtUtc,
    string? PostedBy);

public sealed record StockCountAuthorizationContext(int LocationId, int? CompanyId);

public sealed record StockCountLineView(
    int Id,
    int ItemId,
    string ItemCode,
    string ItemDescription,
    string? BatchNumber,
    DateTime? ExpiryDate,
    int SnapshotQuantity,
    int CurrentQuantity,
    int? CountedQuantity,
    DateTime? CountedAtUtc,
    string? CountedBy,
    bool MovementDetected,
    StockCountVarianceView? Variance = null);

public sealed record StockCountView(
    int Id,
    int LocationId,
    int? CompanyId,
    DateTime SnapshotAtUtc,
    bool MovementDetected,
    IReadOnlyList<StockCountLineView> Lines);
