using Merconiq.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Features.Items.Queries;

public class GetItemsPagedQueryHandler : IRequestHandler<GetItemsPagedQuery, ItemsPagedResult>
{
    private readonly IItemService _itemService;
    private readonly ILogger<GetItemsPagedQueryHandler> _logger;

    public GetItemsPagedQueryHandler(IItemService itemService, ILogger<GetItemsPagedQueryHandler> logger)
    {
        _itemService = itemService;
        _logger = logger;
    }

    public async Task<ItemsPagedResult> Handle(GetItemsPagedQuery request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Handling GetItemsPagedQuery page={Page}, size={Size}", request.Page, request.PageSize);
        var items = await _itemService.GetPagedAsync(request.Page, request.PageSize);
        var totalCount = await _itemService.GetCountAsync();
        return new ItemsPagedResult(items, totalCount);
    }
}
