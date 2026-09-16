using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using MediatR;

namespace Merconiq.Core.Features.Items.Queries;

public class ForecastDemandHandler : IRequestHandler<ForecastDemandQuery, DemandForecastResult>
{
    private readonly IDemandForecastService _service;

    public ForecastDemandHandler(IDemandForecastService service) => _service = service;

    public async Task<DemandForecastResult> Handle(ForecastDemandQuery request, CancellationToken ct)
        => request.CompanyIds is null
            ? await _service.ForecastDemandAsync(request.ItemId, request.HorizonDays)
            : await _service.ForecastDemandForCompaniesAsync(
                request.ItemId, request.HorizonDays, request.CompanyIds);
}

public class ForecastAllItemsDemandHandler : IRequestHandler<ForecastAllItemsDemandQuery, IReadOnlyList<DemandForecastResult>>
{
    private readonly IDemandForecastService _service;

    public ForecastAllItemsDemandHandler(IDemandForecastService service) => _service = service;

    public async Task<IReadOnlyList<DemandForecastResult>> Handle(ForecastAllItemsDemandQuery request, CancellationToken ct)
        => request.CompanyIds is null
            ? await _service.ForecastAllItemsAsync(request.HorizonDays)
            : await _service.ForecastAllItemsForCompaniesAsync(request.HorizonDays, request.CompanyIds);
}
