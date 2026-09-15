using MediatR;

namespace InventoryManagementSystem.Core.Features.Stock.Commands;

public record ReceiveStockCommand(
    int ItemId,
    int LocationId,
    int Quantity,
    string? Notes,
    string? BatchNumber = null,
    DateTime? ExpiryDate = null) : IRequest;
