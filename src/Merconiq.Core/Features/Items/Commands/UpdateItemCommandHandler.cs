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
        if (request.Barcode is not null) item.Barcode = request.Barcode;
        if (request.BaseUnitId.HasValue) item.BaseUnitId = request.BaseUnitId;
        if (request.PurchaseUnitId.HasValue) item.PurchaseUnitId = request.PurchaseUnitId;
        if (request.SalesUnitId.HasValue) item.SalesUnitId = request.SalesUnitId;
        if (request.PurchaseToBaseFactor.HasValue) item.PurchaseToBaseFactor = request.PurchaseToBaseFactor.Value;
        if (request.SalesToBaseFactor.HasValue) item.SalesToBaseFactor = request.SalesToBaseFactor.Value;
        if (request.QuantityPrecision.HasValue) item.QuantityPrecision = request.QuantityPrecision.Value;
        if (request.WholeUnitOnly.HasValue) item.WholeUnitOnly = request.WholeUnitOnly.Value;
        if (request.IsActive.HasValue) item.IsActive = request.IsActive.Value;
        await _itemService.UpdateAsync(item);
    }
}
