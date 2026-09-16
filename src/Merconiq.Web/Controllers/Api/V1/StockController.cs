using Asp.Versioning;
using Merconiq.Core.Entities;
using Merconiq.Core.Features.Stock.Commands;
using Merconiq.Core.Features.Stock.Queries;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Merconiq.Web.Security;

namespace Merconiq.Web.Controllers.Api.V1;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/stock")]
[Produces("application/json")]
[Authorize(Policy = "Api")]
[EnableRateLimiting("Api")]
public class StockController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ICurrentUserAuthorization _authorization;

    public StockController(IMediator mediator, ICurrentUserAuthorization authorization)
    {
        _mediator = mediator;
        _authorization = authorization;
    }

    /// <summary>Get all stock in hand</summary>
    [HttpGet("in-hand")]
    [Authorize(Policy = CapabilityPolicies.View)]
    [ProducesResponseType(typeof(IEnumerable<StockInHand>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll()
    {
        var companyIds = await _authorization.GetAccessibleCompanyIdsAsync(User, CompanyCapability.View);
        var isTenantAdmin = await _authorization.IsTenantAdministratorAsync(User);
        var stock = await _mediator.Send(new GetAllStockQuery(
            isTenantAdmin ? null : companyIds.ToArray()));
        return Ok(ApiResponse<IEnumerable<StockInHand>>.CreateSuccess(stock));
    }

    /// <summary>Get stock at specific item/location</summary>
    [HttpGet("in-hand/{itemId:int}/{locationId:int}")]
    [Authorize(Policy = CapabilityPolicies.View)]
    [ProducesResponseType(typeof(StockInHand), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByItemAndLocation(
        int itemId,
        int locationId,
        [FromQuery] string? batchNumber,
        [FromQuery] DateTime? expiryDate)
    {
        if (!await _authorization.CanAccessLocationAsync(User, locationId, CompanyCapability.View))
        {
            if (await _authorization.IsTenantAdministratorAsync(User))
            {
                return NotFound(ApiResponse<object>.CreateFailure("Location not found."));
            }

            return Forbid();
        }

        var stock = await _mediator.Send(
            new GetStockByItemAndLocationQuery(itemId, locationId, batchNumber, expiryDate));
        return stock is null
            ? NotFound(ApiResponse<StockInHand>.CreateFailure("Stock not found"))
            : Ok(ApiResponse<StockInHand>.CreateSuccess(stock));
    }

    /// <summary>Get stock transactions with optional date filter</summary>
    [HttpGet("transactions")]
    [Authorize(Policy = CapabilityPolicies.View)]
    [ProducesResponseType(typeof(IEnumerable<StockTransaction>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransactions([FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var companyIds = await _authorization.GetAccessibleCompanyIdsAsync(User, CompanyCapability.View);
        var isTenantAdmin = await _authorization.IsTenantAdministratorAsync(User);
        var transactions = await _mediator.Send(new GetStockTransactionsQuery(
            from, to, isTenantAdmin ? null : companyIds.ToArray()));
        return Ok(ApiResponse<IEnumerable<StockTransaction>>.CreateSuccess(transactions));
    }

    /// <summary>Receive stock into a location</summary>
    [HttpPost("receive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = CapabilityPolicies.Post)]
    public async Task<IActionResult> Receive([FromBody] ReceiveStockCommand command, [FromServices] IIdempotencyKeyStore idempotencyKeyStore, [FromServices] ITenantContext tenantContext)
    {
        if (!await _authorization.CanAccessLocationAsync(User, command.LocationId, CompanyCapability.Post))
        {
            return Forbid();
        }

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
            await idempotencyKeyStore.ExecuteAsync(scope, idempotencyKey, IdempotencyRequestHasher.Compute(command), () => _mediator.Send(command), HttpContext.RequestAborted);
        }
        return NoContent();
    }

    /// <summary>Transfer stock between locations</summary>
    [HttpPost("transfer")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = CapabilityPolicies.Post)]
    // This bearer-token API is not cookie-authenticated, so browser CSRF tokens do not apply.
    // Keep the explicit validation marker for static security analysis while opting out at runtime.
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Transfer(
        [FromBody] TransferStockCommand command,
        [FromServices] IIdempotencyKeyStore idempotencyKeyStore,
        [FromServices] ITenantContext tenantContext)
    {
        if (command.FromLocationId == command.ToLocationId)
        {
            return BadRequest(ApiResponse<object>.CreateFailure("Source and destination must be different."));
        }

        if (!await _authorization.CanAccessTransferAsync(
                User, command.FromLocationId, command.ToLocationId, CompanyCapability.Post))
        {
            return Forbid();
        }

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
                () => _mediator.Send(command, HttpContext.RequestAborted),
                HttpContext.RequestAborted);
        }
        return NoContent();
    }

    /// <summary>Sell stock from a location</summary>
    [HttpPost("sell")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = CapabilityPolicies.Post)]
    // This bearer-token API is not cookie-authenticated, so browser CSRF tokens do not apply.
    // Keep the explicit validation marker for static security analysis while opting out at runtime.
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Sell(
        [FromBody] SellStockCommand command,
        [FromServices] IIdempotencyKeyStore idempotencyKeyStore,
        [FromServices] ITenantContext tenantContext)
    {
        if (!await _authorization.CanAccessLocationAsync(User, command.LocationId, CompanyCapability.Post))
        {
            return Forbid();
        }

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
                () => _mediator.Send(command, HttpContext.RequestAborted),
                HttpContext.RequestAborted);
        }
        return NoContent();
    }
}
