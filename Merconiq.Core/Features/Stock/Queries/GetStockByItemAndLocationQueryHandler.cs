using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Features.Stock.Queries;

public class GetStockByItemAndLocationQueryHandler : IRequestHandler<GetStockByItemAndLocationQuery, StockInHand?>
{
    private readonly IStockService _stockService;
    private readonly ILogger<GetStockByItemAndLocationQueryHandler> _logger;

    public GetStockByItemAndLocationQueryHandler(IStockService stockService, ILogger<GetStockByItemAndLocationQueryHandler> logger)
    {
        _stockService = stockService;
        _logger = logger;
    }

    public async Task<StockInHand?> Handle(GetStockByItemAndLocationQuery request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Handling GetStockByItemAndLocationQuery item={ItemId}, loc={LocId}", request.ItemId, request.LocationId);
        return await _stockService.GetByItemAndLocationAsync(
            request.ItemId, request.LocationId, request.BatchNumber, request.ExpiryDate);
    }
}
