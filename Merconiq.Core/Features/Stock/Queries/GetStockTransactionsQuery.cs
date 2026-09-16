using Merconiq.Core.Entities;
using MediatR;

namespace Merconiq.Core.Features.Stock.Queries;

public record GetStockTransactionsQuery(DateTime? From, DateTime? To) : IRequest<IEnumerable<StockTransaction>>;
