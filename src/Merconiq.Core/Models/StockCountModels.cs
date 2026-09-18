using System.ComponentModel.DataAnnotations;

namespace Merconiq.Core.Models;

public sealed record StartStockCountRequest(
    [property: Range(1, int.MaxValue)] int LocationId);

public sealed record RecordStockCountObservationRequest(
    [property: Range(0, int.MaxValue)] int CountedQuantity);

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
    bool MovementDetected);

public sealed record StockCountView(
    int Id,
    int LocationId,
    int? CompanyId,
    DateTime SnapshotAtUtc,
    bool MovementDetected,
    IReadOnlyList<StockCountLineView> Lines);
