using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Features.Items.Commands;

public class UpdateItemCommandHandler : IRequestHandler<UpdateItemCommand>
{
    private readonly IItemService _itemService;
    private readonly ILogger<UpdateItemCommandHandler> _logger;

    public UpdateItemCommandHandler(IItemService itemService, ILogger<UpdateItemCommandHandler> logger)
    {
        _itemService = itemService;
        _logger = logger;
    }

    public async Task Handle(UpdateItemCommand request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Handling UpdateItemCommand for Id={Id}", request.Id);
        var item = await _itemService.GetByIdAsync(request.Id);
        if (item is null)
            throw new KeyNotFoundException($"Item with Id={request.Id} not found");

        item.Description = request.Description;
        item.Rate = request.Rate;
        item.SupplierId = request.SupplierId;
        item.BaseUnitId = request.BaseUnitId;
        item.PurchaseUnitId = request.PurchaseUnitId;
        item.SalesUnitId = request.SalesUnitId;
        item.PurchaseToBaseFactor = request.PurchaseToBaseFactor;
        item.SalesToBaseFactor = request.SalesToBaseFactor;
        item.QuantityPrecision = request.QuantityPrecision;
        item.WholeUnitOnly = request.WholeUnitOnly;
        await _itemService.UpdateAsync(item);
    }
}
