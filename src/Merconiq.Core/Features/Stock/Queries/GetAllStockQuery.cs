using Merconiq.Core.Entities;
using MediatR;

namespace Merconiq.Core.Features.Stock.Queries;

public record GetAllStockQuery(IReadOnlyCollection<int>? CompanyIds = null) : IRequest<IEnumerable<StockInHand>>;
