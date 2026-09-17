using Merconiq.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Features.Stock.Commands;

public class SellStockCommandHandler : IRequestHandler<SellStockCommand>
{
    private readonly IStockService _stockService;
    private readonly ILogger<SellStockCommandHandler> _logger;

    public SellStockCommandHandler(IStockService stockService, ILogger<SellStockCommandHandler> logger)
    {
        _stockService = stockService;
        _logger = logger;
    }

    public async Task Handle(SellStockCommand request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Handling SellStockCommand item={ItemId}, loc={LocId}, qty={Qty}", request.ItemId, request.LocationId, request.Quantity);
        await _stockService.SellStockAsync(
            request.ItemId, request.LocationId, request.Quantity, request.Notes,
            request.BatchNumber, request.ExpiryDate, mutationScope: request.MutationScope,
            expiryExceptionReason: request.ExpiryExceptionReason);
    }
}
