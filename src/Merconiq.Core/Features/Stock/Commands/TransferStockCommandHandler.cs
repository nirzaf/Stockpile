using Merconiq.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Features.Stock.Commands;

public class TransferStockCommandHandler : IRequestHandler<TransferStockCommand>
{
    private readonly IStockService _stockService;
    private readonly ILogger<TransferStockCommandHandler> _logger;

    public TransferStockCommandHandler(IStockService stockService, ILogger<TransferStockCommandHandler> logger)
    {
        _stockService = stockService;
        _logger = logger;
    }

    public async Task Handle(TransferStockCommand request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Handling TransferStockCommand item={ItemId}, from={From}, to={To}, qty={Qty}",
            request.ItemId, request.FromLocationId, request.ToLocationId, request.Quantity);
        await _stockService.TransferStockAsync(
            request.ItemId, request.FromLocationId, request.ToLocationId, request.Quantity, request.Notes,
            request.BatchNumber, request.ExpiryDate, request.MutationScope, request.ExpiryExceptionReason);
    }
}
