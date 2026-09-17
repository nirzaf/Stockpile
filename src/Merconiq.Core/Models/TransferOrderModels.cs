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
    int ReceivedQuantity = 0);

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
