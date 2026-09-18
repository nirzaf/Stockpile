using Merconiq.Core.Entities;

namespace Merconiq.Core.Models;

public sealed record TransferOrderLineRequest(
    int ItemId,
    int Quantity,
    string? BatchNumber = null,
    DateTime? ExpiryDate = null,
    int? LineId = null);

public sealed record CreateTransferOrderRequest(
    int CompanyId,
    int FromLocationId,
    int ToLocationId,
    IReadOnlyCollection<TransferOrderLineRequest> Lines,
    string? Notes = null);

public sealed record TransferOrderLineView(
    int Id,
    Guid DocumentLineId,
    int ItemId,
    int Quantity,
    string? BatchNumber,
    DateTime? ExpiryDate,
    string ReservationSourceLineReference,
    int DispatchedQuantity = 0,
    int ReceivedQuantity = 0)
{
    public IReadOnlyList<TransferTransitEntryView> TransitEntries { get; init; } = [];
}

/// <summary>Read model for one immutable dispatch and its still-outstanding quantity/value.</summary>
public sealed record TransferTransitEntryView(
    int Id,
    Guid SourceDocumentLineId,
    int Quantity,
    int ReceivedQuantity,
    int QuarantinedQuantity,
    int ReturnedQuantity,
    int RemainingQuantity,
    string? BatchNumber,
    DateTime? ExpiryDate,
    decimal UnitCost,
    decimal TotalValue,
    decimal RemainingValue,
    string DispatchedBy,
    DateTimeOffset DispatchedAt);

public sealed record TransferOrderView(
    int Id,
    Guid DocumentId,
    string Number,
    int CompanyId,
    int FromLocationId,
    int ToLocationId,
    DateTime OrderDate,
    TransferOrderStatus Status,
    string? Notes,
    IReadOnlyList<TransferOrderLineView> Lines);
