using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using MediatR;

namespace Merconiq.Core.Features.Stock.Queries;

public class DetectAnomaliesHandler : IRequestHandler<DetectAnomaliesQuery, IReadOnlyList<StockAnomaly>>
{
    private readonly IAnomalyDetectionService _service;

    public DetectAnomaliesHandler(IAnomalyDetectionService service) => _service = service;

    public async Task<IReadOnlyList<StockAnomaly>> Handle(DetectAnomaliesQuery request, CancellationToken ct)
        => request.CompanyIds is null
            ? await _service.DetectAnomaliesAsync(request.From, request.To)
            : await _service.DetectAnomaliesForCompaniesAsync(
                request.From, request.To, request.CompanyIds);
}
