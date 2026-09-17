using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Features.Items.Commands;

public class CreateItemCommandHandler : IRequestHandler<CreateItemCommand, Item>
{
    private readonly IItemService _itemService;
    private readonly ILogger<CreateItemCommandHandler> _logger;

    public CreateItemCommandHandler(IItemService itemService, ILogger<CreateItemCommandHandler> logger)
    {
        _itemService = itemService;
        _logger = logger;
    }

    public async Task<Item> Handle(CreateItemCommand request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Handling CreateItemCommand for code={Code}", request.ItemCode);
        var item = new Item
        {
            ItemCode = request.ItemCode,
            Description = request.Description,
            Rate = request.Rate,
            Barcode = request.Barcode,
            SupplierId = request.SupplierId,
            BaseUnitId = request.BaseUnitId,
            PurchaseUnitId = request.PurchaseUnitId,
            SalesUnitId = request.SalesUnitId,
            PurchaseToBaseFactor = request.PurchaseToBaseFactor,
            SalesToBaseFactor = request.SalesToBaseFactor,
            QuantityPrecision = request.QuantityPrecision,
            WholeUnitOnly = request.WholeUnitOnly
        };
        return await _itemService.CreateAsync(item);
    }
}
