using MediatR;

namespace Merconiq.Core.Features.Stock.Commands;

public record TransferStockCommand(
    int ItemId,
    int FromLocationId,
    int ToLocationId,
    int Quantity,
    string? Notes,
    string? BatchNumber = null,
    DateTime? ExpiryDate = null) : IRequest;
