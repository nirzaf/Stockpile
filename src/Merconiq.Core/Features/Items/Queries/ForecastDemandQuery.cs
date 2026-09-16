using Merconiq.Core.Models;
using MediatR;

namespace Merconiq.Core.Features.Items.Queries;

public record ForecastDemandQuery(
    int ItemId,
    int HorizonDays = 30,
    IReadOnlyCollection<int>? CompanyIds = null) : IRequest<DemandForecastResult>;

public record ForecastAllItemsDemandQuery(
    int HorizonDays = 30,
    IReadOnlyCollection<int>? CompanyIds = null) : IRequest<IReadOnlyList<DemandForecastResult>>;
