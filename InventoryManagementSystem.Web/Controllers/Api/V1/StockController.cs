using Asp.Versioning;
using InventoryManagementSystem.Core.Entities;
using InventoryManagementSystem.Core.Features.Stock.Commands;
using InventoryManagementSystem.Core.Features.Stock.Queries;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Core.Models;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using InventoryManagementSystem.Web.Security;

namespace InventoryManagementSystem.Web.Controllers.Api.V1;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/stock")]
[Produces("application/json")]
[Authorize(Policy = "Api")]
[EnableRateLimiting("Api")]
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
        return Ok(ApiResponse<IEnumerable<StockInHand>>.CreateSuccess(stock));
    }

    /// <summary>Get stock at specific item/location</summary>
    [HttpGet("in-hand/{itemId:int}/{locationId:int}")]
    [ProducesResponseType(typeof(StockInHand), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByItemAndLocation(
        int itemId,
        int locationId,
        [FromQuery] string? batchNumber,
        [FromQuery] DateTime? expiryDate)
    {
        var stock = await _mediator.Send(
            new GetStockByItemAndLocationQuery(itemId, locationId, batchNumber, expiryDate));
        return stock is null
            ? NotFound(ApiResponse<StockInHand>.CreateFailure("Stock not found"))
            : Ok(ApiResponse<StockInHand>.CreateSuccess(stock));
    }

    /// <summary>Get stock transactions with optional date filter</summary>
    [HttpGet("transactions")]
    [ProducesResponseType(typeof(IEnumerable<StockTransaction>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransactions([FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var transactions = await _mediator.Send(new GetStockTransactionsQuery(from, to));
        return Ok(ApiResponse<IEnumerable<StockTransaction>>.CreateSuccess(transactions));
    }

    /// <summary>Receive stock into a location</summary>
    [HttpPost("receive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Roles = "Admin,Manager,Staff")]
    public async Task<IActionResult> Receive([FromBody] ReceiveStockCommand command, [FromServices] IIdempotencyKeyStore idempotencyKeyStore, [FromServices] ITenantContext tenantContext)
    {
        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
        if (idempotencyKey.Length > 200)
        {
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key must be 200 characters or fewer."));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await _mediator.Send(command);
        }
        else
        {
            var scope = $"{tenantContext.TenantId}:{Request.Method}:{Request.Path}";
            await idempotencyKeyStore.ExecuteAsync(scope, idempotencyKey, IdempotencyRequestHasher.Compute(command), () => _mediator.Send(command));
        }
        return NoContent();
    }

    /// <summary>Transfer stock between locations</summary>
    [HttpPost("transfer")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Roles = "Admin,Manager,Staff")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Transfer(
        [FromBody] TransferStockCommand command,
        [FromServices] IIdempotencyKeyStore idempotencyKeyStore,
        [FromServices] ITenantContext tenantContext)
    {
        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
        if (idempotencyKey.Length > 200)
        {
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key must be 200 characters or fewer."));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await _mediator.Send(command);
        }
        else
        {
            var scope = $"{tenantContext.TenantId}:{Request.Method}:{Request.Path}";
            await idempotencyKeyStore.ExecuteAsync(
                scope,
                idempotencyKey,
                IdempotencyRequestHasher.Compute(command),
                () => _mediator.Send(command));
        }
        return NoContent();
    }

    /// <summary>Sell stock from a location</summary>
    [HttpPost("sell")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Roles = "Admin,Manager,Staff")]
    public async Task<IActionResult> Sell([FromBody] SellStockCommand command)
    {
        await _mediator.Send(command);
        return NoContent();
    }
}
