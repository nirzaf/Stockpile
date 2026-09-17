using Merconiq.Core.Entities;

namespace Merconiq.Core.Models;

public sealed record CreateStockReservationRequest(
    int ItemId,
    int LocationId,
    int Quantity,
    string SourceLineReference,
    string? BatchNumber = null,
    DateTime? ExpiryDate = null,
    DateTimeOffset? ExpiresAt = null,
    string? ExpiryExceptionReason = null);

public sealed record StockReservationActionRequest(string SourceLineReference, string? Reason = null);

public sealed record ConsumeStockReservationRequest(
    string SourceLineReference,
    int Quantity,
    string? Notes = null,
    string? ExpiryExceptionReason = null);

public sealed record StockReservationView(
    int Id,
    int ItemId,
    int LocationId,
    string SourceLineReference,
    string? BatchNumber,
    DateTime? ExpiryDate,
    int Quantity,
    int ConsumedQuantity,
    int RemainingQuantity,
    DateTimeOffset ExpiresAt,
    StockReservationStatus Status,
    string? ExpiryExceptionReason = null,
    IReadOnlyList<StockReservationAllocationView>? Allocations = null);

public sealed record StockReservationAllocationView(
    string? BatchNumber,
    DateTime? ExpiryDate,
    int Quantity,
    int ConsumedQuantity,
    int RemainingQuantity,
    string? ExpiryExceptionReason);

public sealed record StockAvailabilityView(
    int ItemId,
    int LocationId,
    string? BatchNumber,
    DateTime? ExpiryDate,
    int OnHand,
    int Reserved,
    int Quarantined,
    int Available);
