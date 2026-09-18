using Merconiq.Core.Entities;

namespace Merconiq.Core.Models;

public sealed record DispatchTransferOrderRequest(int Quantity);

public sealed record TransferTransitSettlementRequest(
    int Quantity,
    TransferTransitSettlementType SettlementType,
    string? BatchNumber = null,
    DateTime? ExpiryDate = null,
    string? Reason = null,
    string? Notes = null);

public sealed record TransferTransitStockMovementRequest(
    int ItemId,
    int FromLocationId,
    int ToLocationId,
    int Quantity,
    string? BatchNumber,
    DateTime? ExpiryDate,
    decimal UnitCost,
    decimal TotalValue,
    string SourceLineReference,
    string Notes,
    TransactionType TransactionType,
    string? QuarantineReason,
    StockMutationScope MutationScope);

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

public sealed record TransferTransitSettlementView(
    int Id,
    Guid? DocumentId,
    string? DocumentNumber,
    Guid? DocumentLineId,
    int TransferTransitEntryId,
    int TransferOrderId,
    int TransferOrderLineId,
    Guid SourceDocumentLineId,
    int CompanyId,
    int ItemId,
    int FromLocationId,
    int ToLocationId,
    int StockTransactionId,
    int Quantity,
    TransferTransitSettlementType SettlementType,
    string? BatchNumber,
    DateTime? ExpiryDate,
    decimal UnitCost,
    decimal TotalValue,
    string IdempotencyKey,
    string SettledBy,
    DateTimeOffset SettledAt,
    string? Reason);
