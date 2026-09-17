namespace Merconiq.Core.Models;

public sealed record DispatchTransferOrderRequest(int Quantity);

public sealed record TransferStockDispatchMovement(
    int StockTransactionId,
    int Quantity,
    string? BatchNumber,
    DateTime? ExpiryDate,
    decimal UnitCost,
    decimal TotalValue);

public sealed record TransferDispatchView(
    int Id,
    int TransferOrderId,
    int TransferOrderLineId,
    Guid SourceDocumentLineId,
    int CompanyId,
    int ItemId,
    int FromLocationId,
    int ToLocationId,
    int StockTransactionId,
    int Quantity,
    string? BatchNumber,
    DateTime? ExpiryDate,
    decimal UnitCost,
    decimal TotalValue,
    string IdempotencyKey,
    string DispatchedBy,
    DateTimeOffset DispatchedAt);

public sealed record ReceiveTransferTransitRequest(int Quantity);

public sealed record TransferTransitReceiptView(
    int Id,
    int TransferOrderId,
    int TransferOrderLineId,
    int TransferTransitEntryId,
    Guid SourceDocumentLineId,
    int CompanyId,
    int ItemId,
    int FromLocationId,
    int ToLocationId,
    int StockTransactionId,
    int Quantity,
    int RemainingQuantity,
    string? BatchNumber,
    DateTime? ExpiryDate,
    decimal UnitCost,
    decimal TotalValue,
    decimal RemainingValue,
    string IdempotencyKey,
    string ReceivedBy,
    DateTimeOffset ReceivedAt);
