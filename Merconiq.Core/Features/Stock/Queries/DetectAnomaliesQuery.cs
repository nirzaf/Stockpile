using Merconiq.Core.Models;
using MediatR;

namespace Merconiq.Core.Features.Stock.Queries;

public record DetectAnomaliesQuery(DateTime? From = null, DateTime? To = null) : IRequest<IReadOnlyList<StockAnomaly>>;
