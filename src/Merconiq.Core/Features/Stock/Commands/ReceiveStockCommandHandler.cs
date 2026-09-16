using Merconiq.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Features.Stock.Commands;

public class ReceiveStockCommandHandler : IRequestHandler<ReceiveStockCommand>
{
    private readonly IStockService _stockService;
    private readonly ILogger<ReceiveStockCommandHandler> _logger;

    public ReceiveStockCommandHandler(IStockService stockService, ILogger<ReceiveStockCommandHandler> logger)
    {
        _stockService = stockService;
        _logger = logger;
    }

    public async Task Handle(ReceiveStockCommand request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Handling ReceiveStockCommand item={ItemId}, loc={LocId}, qty={Qty}", request.ItemId, request.LocationId, request.Quantity);
        await _stockService.ReceiveStockAsync(
            request.ItemId, request.LocationId, request.Quantity, request.Notes,
            request.BatchNumber, request.ExpiryDate, request.UnitCost);
    }
}
