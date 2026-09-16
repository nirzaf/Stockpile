using Merconiq.Core.Entities;
using MediatR;

namespace Merconiq.Core.Features.Stock.Queries;

public record GetAllStockQuery : IRequest<IEnumerable<StockInHand>>;
