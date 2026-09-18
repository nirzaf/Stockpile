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
    private readonly IStockService? _stock;

    public StockController(
        IMediator mediator,
        ICurrentUserAuthorization authorization,
        IStockService? stock = null)
    {
        _mediator = mediator;
        _authorization = authorization;
        _stock = stock;
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

    /// <summary>Get moving-average buckets and immutable valued movement evidence.</summary>
    [HttpGet("valuation")]
    [Authorize(Policy = CapabilityPolicies.View)]
    [ProducesResponseType(typeof(IEnumerable<StockValuationView>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetValuation(
        [FromQuery] int? itemId,
        [FromQuery] int? locationId)
    {
        if (locationId.HasValue && !await _authorization.CanAccessLocationAsync(
                User, locationId.Value, CompanyCapability.View))
        {
            if (await _authorization.IsTenantAdministratorAsync(User))
                return NotFound(ApiResponse<object>.CreateFailure("Location not found."));

            return Forbid();
        }

        var stock = _stock ?? throw new InvalidOperationException("Stock service is not configured.");
        var companyIds = await _authorization.GetAccessibleCompanyIdsAsync(User, CompanyCapability.View);
        var valuation = await stock.GetValuationAsync(
            itemId,
            locationId,
            await _authorization.IsTenantAdministratorAsync(User) ? null : companyIds);
        return Ok(ApiResponse<IEnumerable<StockValuationView>>.CreateSuccess(valuation));
    }

    /// <summary>Receive stock into a location</summary>
    /// <remarks>
    /// Requires an <c>Idempotency-Key</c> header (maximum 200 characters). Retry the same key with
    /// the same request body to replay a completed receive.
    /// </remarks>
    [HttpPost("receive")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = CapabilityPolicies.Post)]
    public async Task<IActionResult> Receive([FromBody] ReceiveStockCommand command, [FromServices] IIdempotencyKeyStore idempotencyKeyStore, [FromServices] ITenantContext tenantContext)
    {
        var cancellationToken = HttpContext.RequestAborted;
        var mutationScope = new StockMutationScope(
            await _authorization.GetLocationCompanyIdAsync(User, command.LocationId),
            token => _authorization.CanAccessLocationAsync(
                User, command.LocationId, CompanyCapability.Post, token));
        if (!await _authorization.CanAccessLocationAsync(
                User, command.LocationId, CompanyCapability.Post, cancellationToken))
        {
            return Forbid();
        }
        var authorizedCommand = command with { MutationScope = mutationScope };

        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key is required for stock receive."));
        }

        if (idempotencyKey.Length > 200)
        {
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key must be 200 characters or fewer."));
        }

        var scope = $"{tenantContext.TenantId}:{Request.Method}:{Request.Path}";
        await idempotencyKeyStore.ExecuteAsync(
            scope,
            idempotencyKey,
            IdempotencyRequestHasher.Compute(command),
            () => _mediator.Send(authorizedCommand, cancellationToken),
            cancellationToken);
        return NoContent();
    }

    /// <summary>Immediate operational transfer; controlled transfer orders reserve before dispatch.</summary>
    /// <remarks>
    /// Keep this endpoint for documented immediate operational moves. Use transfer orders when
    /// approval and source reservation must precede dispatch; this endpoint does not bypass the
    /// existing same-company authorization and stock-availability checks.
    /// </remarks>
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

        var mutationScope = new StockMutationScope(
            await _authorization.GetLocationCompanyIdAsync(User, command.FromLocationId),
            () => _authorization.CanAccessTransferAsync(
                User, command.FromLocationId, command.ToLocationId, CompanyCapability.Post));
        if (!await _authorization.CanAccessTransferAsync(
                User, command.FromLocationId, command.ToLocationId, CompanyCapability.Post))
        {
            return Forbid();
        }
        if (!string.IsNullOrWhiteSpace(command.ExpiryExceptionReason) &&
            !await _authorization.CanOverrideExpiredStockAtLocationAsync(User, command.FromLocationId))
        {
            return Forbid();
        }

        mutationScope = mutationScope with
        {
            ReauthorizeExpiredStockOverride = () => _authorization.CanOverrideExpiredStockAtLocationAsync(
                User, command.FromLocationId)
        };

        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
        if (idempotencyKey.Length > 200)
        {
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key must be 200 characters or fewer."));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await _mediator.Send(command with { MutationScope = mutationScope }, HttpContext.RequestAborted);
        }
        else
        {
            var scope = $"{tenantContext.TenantId}:{Request.Method}:{Request.Path}";
            await idempotencyKeyStore.ExecuteAsync(
                scope,
                idempotencyKey,
                IdempotencyRequestHasher.Compute(command),
                () => _mediator.Send(command with { MutationScope = mutationScope }, HttpContext.RequestAborted),
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
        var cancellationToken = HttpContext.RequestAborted;
        var mutationScope = new StockMutationScope(
            await _authorization.GetLocationCompanyIdAsync(User, command.LocationId),
            token => _authorization.CanAccessLocationAsync(
                User, command.LocationId, CompanyCapability.Post, token));
        if (!await _authorization.CanAccessLocationAsync(
                User, command.LocationId, CompanyCapability.Post, cancellationToken))
        {
            return Forbid();
        }
        if (!string.IsNullOrWhiteSpace(command.ExpiryExceptionReason) &&
            !await _authorization.CanOverrideExpiredStockAtLocationAsync(User, command.LocationId))
        {
            return Forbid();
        }
        mutationScope = mutationScope with
        {
            ReauthorizeExpiredStockOverride = () => _authorization.CanOverrideExpiredStockAtLocationAsync(
                User, command.LocationId)
        };

        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
        if (idempotencyKey.Length > 200)
        {
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key must be 200 characters or fewer."));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await _mediator.Send(command with { MutationScope = mutationScope }, HttpContext.RequestAborted);
        }
        else
        {
            var scope = $"{tenantContext.TenantId}:{Request.Method}:{Request.Path}";
            await idempotencyKeyStore.ExecuteAsync(
                scope,
                idempotencyKey,
                IdempotencyRequestHasher.Compute(command),
                () => _mediator.Send(command with { MutationScope = mutationScope }, HttpContext.RequestAborted),
                HttpContext.RequestAborted);
        }
        return NoContent();
    }

    /// <summary>Moves available stock into quarantine without changing on-hand quantity.</summary>
    [HttpPost("quarantine")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = CapabilityPolicies.Post)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Quarantine(
        [FromBody] ChangeStockQuarantineRequest request,
        [FromServices] IIdempotencyKeyStore idempotencyKeyStore,
        [FromServices] ITenantContext tenantContext)
    {
        var cancellationToken = HttpContext.RequestAborted;
        var companyId = await _authorization.GetLocationCompanyIdAsync(User, request.LocationId);
        if (!companyId.HasValue || !await _authorization.CanAccessLocationAsync(
                User, request.LocationId, CompanyCapability.Post, cancellationToken))
        {
            return Forbid();
        }

        var mutationScope = new StockMutationScope(
            companyId,
            token => _authorization.CanAccessLocationAsync(
                User, request.LocationId, CompanyCapability.Post, token));
        var command = new QuarantineStockCommand(
            request.ItemId,
            request.LocationId,
            request.Quantity,
            request.SourceLineReference,
            request.BatchNumber,
            request.ExpiryDate,
            request.Reason,
            mutationScope);
        return await ExecuteQuarantineCommandAsync(command, request, idempotencyKeyStore, tenantContext);
    }

    /// <summary>Releases quarantined stock after a separately granted company override.</summary>
    [HttpPost("quarantine/release")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [Authorize(Policy = CapabilityPolicies.Post)]
    [Authorize(Policy = CapabilityPolicies.OverrideQuarantinedStock)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> ReleaseQuarantine(
        [FromBody] ChangeStockQuarantineRequest request,
        [FromServices] IIdempotencyKeyStore idempotencyKeyStore,
        [FromServices] ITenantContext tenantContext)
    {
        var cancellationToken = HttpContext.RequestAborted;
        var companyId = await _authorization.GetLocationCompanyIdAsync(User, request.LocationId);
        if (!companyId.HasValue || !await _authorization.CanAccessLocationAsync(
                User, request.LocationId, CompanyCapability.Post, cancellationToken) ||
            !await _authorization.CanOverrideQuarantinedStockAtLocationAsync(User, request.LocationId))
        {
            return Forbid();
        }

        var mutationScope = new StockMutationScope(
            companyId,
            token => _authorization.CanAccessLocationAsync(
                User, request.LocationId, CompanyCapability.Post, token),
            ReauthorizeQuarantinedStockOverride: () => _authorization.CanOverrideQuarantinedStockAtLocationAsync(
                User, request.LocationId));
        var command = new ReleaseQuarantinedStockCommand(
            request.ItemId,
            request.LocationId,
            request.Quantity,
            request.SourceLineReference,
            request.BatchNumber,
            request.ExpiryDate,
            request.Reason,
            mutationScope);
        return await ExecuteQuarantineCommandAsync(command, request, idempotencyKeyStore, tenantContext);
    }

    private async Task<IActionResult> ExecuteQuarantineCommandAsync<TCommand>(
        TCommand command,
        ChangeStockQuarantineRequest request,
        IIdempotencyKeyStore idempotencyKeyStore,
        ITenantContext tenantContext)
        where TCommand : IRequest
    {
        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
        if (idempotencyKey.Length > 200)
        {
            return BadRequest(ApiResponse<object>.CreateFailure(
                "Idempotency-Key must be 200 characters or fewer."));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await _mediator.Send(command, HttpContext.RequestAborted);
        }
        else
        {
            var scope = $"{tenantContext.TenantId}:{Request.Method}:{Request.Path}";
            await idempotencyKeyStore.ExecuteAsync(
                scope,
                idempotencyKey,
                IdempotencyRequestHasher.Compute(request),
                () => _mediator.Send(command, HttpContext.RequestAborted),
                HttpContext.RequestAborted);
        }

        return NoContent();
    }
}
