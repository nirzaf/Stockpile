using Merconiq.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Features.Items.Commands;

public class DeleteItemCommandHandler : IRequestHandler<DeleteItemCommand>
{
    private readonly IItemService _itemService;
    private readonly ILogger<DeleteItemCommandHandler> _logger;

    public DeleteItemCommandHandler(IItemService itemService, ILogger<DeleteItemCommandHandler> logger)
    {
        _itemService = itemService;
        _logger = logger;
    }

    public async Task Handle(DeleteItemCommand request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Handling DeleteItemCommand for Id={Id}", request.Id);
        await _itemService.DeleteAsync(request.Id);
    }
}
