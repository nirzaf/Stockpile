using MediatR;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Features.Stock.Commands;

public sealed class ReleaseQuarantinedStockCommandHandler(
    IStockService stockService,
    ILogger<ReleaseQuarantinedStockCommandHandler> logger) : IRequestHandler<ReleaseQuarantinedStockCommand>
{
    public async Task Handle(ReleaseQuarantinedStockCommand request, CancellationToken cancellationToken)
    {
        logger.LogDebug("Handling ReleaseQuarantinedStockCommand item={ItemId}, loc={LocationId}, qty={Quantity}",
            request.ItemId, request.LocationId, request.Quantity);
        await stockService.ReleaseQuarantinedStockAsync(
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
