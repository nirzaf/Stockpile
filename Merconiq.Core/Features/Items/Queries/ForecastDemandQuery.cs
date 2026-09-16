using Merconiq.Core.Models;
using MediatR;

namespace Merconiq.Core.Features.Items.Queries;

public record ForecastDemandQuery(int ItemId, int HorizonDays = 30) : IRequest<DemandForecastResult>;

public record ForecastAllItemsDemandQuery(int HorizonDays = 30) : IRequest<IReadOnlyList<DemandForecastResult>>;
