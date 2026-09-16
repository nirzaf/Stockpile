using Merconiq.Core.Entities;
using MediatR;

namespace Merconiq.Core.Features.Stock.Queries;

public record GetStockByItemAndLocationQuery(
    int ItemId,
    int LocationId,
    string? BatchNumber = null,
    DateTime? ExpiryDate = null) : IRequest<StockInHand?>;
