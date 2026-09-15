using InventoryManagementSystem.Core.Entities;
using MediatR;

namespace InventoryManagementSystem.Core.Features.Stock.Queries;

public record GetStockByItemAndLocationQuery(
    int ItemId,
    int LocationId,
    string? BatchNumber = null,
    DateTime? ExpiryDate = null) : IRequest<StockInHand?>;
