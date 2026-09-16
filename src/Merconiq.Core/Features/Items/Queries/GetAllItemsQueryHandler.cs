using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Features.Items.Queries;

/// <summary>
/// Adapter handler — delegates to existing IItemService.
/// </summary>
public class GetAllItemsQueryHandler : IRequestHandler<GetAllItemsQuery, IEnumerable<Item>>
{
    private readonly IItemService _itemService;
    private readonly ILogger<GetAllItemsQueryHandler> _logger;

    public GetAllItemsQueryHandler(IItemService itemService, ILogger<GetAllItemsQueryHandler> logger)
    {
        _itemService = itemService;
        _logger = logger;
    }

    public async Task<IEnumerable<Item>> Handle(GetAllItemsQuery request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Handling GetAllItemsQuery");
        return await _itemService.GetAllAsync();
    }
}
