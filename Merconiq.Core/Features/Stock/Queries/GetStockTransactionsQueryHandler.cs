using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Merconiq.Core.Features.Stock.Queries;

public class GetStockTransactionsQueryHandler : IRequestHandler<GetStockTransactionsQuery, IEnumerable<StockTransaction>>
{
    private readonly IStockService _stockService;
    private readonly ILogger<GetStockTransactionsQueryHandler> _logger;

    public GetStockTransactionsQueryHandler(IStockService stockService, ILogger<GetStockTransactionsQueryHandler> logger)
    {
        _stockService = stockService;
        _logger = logger;
    }

    public async Task<IEnumerable<StockTransaction>> Handle(GetStockTransactionsQuery request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Handling GetStockTransactionsQuery from={From}, to={To}", request.From, request.To);
        return await _stockService.GetTransactionsAsync(request.From, request.To);
    }
}
