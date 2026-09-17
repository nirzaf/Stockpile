using MediatR;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Features.Stock.Commands;

public sealed class QuarantineStockCommandHandler(
    IStockService stockService,
    ILogger<QuarantineStockCommandHandler> logger) : IRequestHandler<QuarantineStockCommand>
{
    public async Task Handle(QuarantineStockCommand request, CancellationToken cancellationToken)
    {
        logger.LogDebug("Handling QuarantineStockCommand item={ItemId}, loc={LocationId}, qty={Quantity}",
            request.ItemId, request.LocationId, request.Quantity);
        await stockService.QuarantineStockAsync(
            new ChangeStockQuarantineRequest(
                request.ItemId,
                request.LocationId,
                request.Quantity,
                request.SourceLineReference,
                request.BatchNumber,
                request.ExpiryDate,
                request.Reason),
            request.MutationScope);
    }
}
