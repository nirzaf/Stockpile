using Asp.Versioning;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Features.Stock.Commands;
using InventoryManagementSystem.Core.Features.Stock.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InventoryManagementSystem.Web.Controllers.Api.V1;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/stock")]
[Produces("application/json")]
[Authorize]
public class StockController : ControllerBase
{
    private readonly IMediator _mediator;

    public StockController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>Get all stock in hand</summary>
    [HttpGet("in-hand")]
    [ProducesResponseType(typeof(IEnumerable<StockInHand>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll()
    {
        var stock = await _mediator.Send(new GetAllStockQuery());
        return Ok(stock);
    }

    /// <summary>Get stock at specific item/location</summary>
    [HttpGet("in-hand/{itemId:int}/{locationId:int}")]
    [ProducesResponseType(typeof(StockInHand), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByItemAndLocation(int itemId, int locationId)
    {
        var stock = await _mediator.Send(new GetStockByItemAndLocationQuery(itemId, locationId));
        return stock is null ? NotFound() : Ok(stock);
    }

    /// <summary>Get stock transactions with optional date filter</summary>
    [HttpGet("transactions")]
    [ProducesResponseType(typeof(IEnumerable<StockTransaction>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransactions([FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var transactions = await _mediator.Send(new GetStockTransactionsQuery(from, to));
        return Ok(transactions);
    }

    /// <summary>Receive stock into a location</summary>
    [HttpPost("receive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Receive([FromBody] ReceiveStockCommand command)
    {
        await _mediator.Send(command);
        return NoContent();
    }

    /// <summary>Transfer stock between locations</summary>
    [HttpPost("transfer")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Transfer([FromBody] TransferStockCommand command)
    {
        await _mediator.Send(command);
        return NoContent();
    }

    /// <summary>Sell stock from a location</summary>
    [HttpPost("sell")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Sell([FromBody] SellStockCommand command)
    {
        await _mediator.Send(command);
        return NoContent();
    }
}
