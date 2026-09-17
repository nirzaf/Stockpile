using Asp.Versioning;
using System.Security.Claims;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Merconiq.Web.Controllers.Api.V1;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/transfer-orders")]
[Produces("application/json")]
[Authorize(Policy = "Api")]
[EnableRateLimiting("Api")]
public sealed class TransferOrdersController(
    ITransferOrderService transferOrders,
    ICurrentUserAuthorization authorization,
    IIdempotencyKeyStore idempotencyKeyStore,
    ITenantContext tenantContext) : ControllerBase
{
    [HttpGet("{id:int}")]
    [Authorize(Policy = CapabilityPolicies.View)]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var order = await transferOrders.GetByIdAsync(id, cancellationToken);
        if (order is null)
            return NotFound(ApiResponse<object>.CreateFailure("Transfer order not found."));
        if (!await authorization.CanAccessCompanyAsync(User, order.CompanyId, CompanyCapability.View) ||
            !await authorization.CanAccessTransferAsync(
                User, order.FromLocationId, order.ToLocationId, CompanyCapability.View))
            return await AccessFailureAsync();
        return Ok(ApiResponse<TransferOrderView>.CreateSuccess(order));
    }

    [HttpPost]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Create(
        [FromBody] CreateTransferOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanEditTransferAsync(request.FromLocationId, request.ToLocationId, request.CompanyId))
            return Forbid();
        var key = Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key is required."));
        if (key.Length > 200)
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key must be 200 characters or fewer."));

        // The numbered document identity owns create idempotency so the response can
        // return the replayed order, unlike the no-content mutation coordinator.
        var order = await transferOrders.CreateAsync(request, key, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = order.Id },
            ApiResponse<TransferOrderView>.CreateSuccess(order));
    }

    [HttpPut("{id:int}")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Amend(
        int id,
        [FromBody] CreateTransferOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (!await CanEditTransferAsync(request.FromLocationId, request.ToLocationId, request.CompanyId))
            return Forbid();
        var scope = CreateMutationScope(request.CompanyId, request.FromLocationId, request.ToLocationId, CompanyCapability.Post);
        return await RunMutationAsync(request, scope, () => transferOrders.AmendAsync(id, request, scope, cancellationToken), cancellationToken);
    }

    [HttpPost("{id:int}/approve")]
    [Authorize(Policy = CapabilityPolicies.Approve)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Approve(int id, CancellationToken cancellationToken)
    {
        var order = await transferOrders.GetByIdAsync(id, cancellationToken);
        if (order is null)
            return NotFound(ApiResponse<object>.CreateFailure("Transfer order not found."));
        if (!await authorization.CanAccessTransferAsync(
                User, order.FromLocationId, order.ToLocationId, CompanyCapability.Approve))
            return Forbid();
        var scope = CreateMutationScope(order.CompanyId, order.FromLocationId, order.ToLocationId, CompanyCapability.Approve);
        return await RunMutationAsync(new { id }, scope, () => transferOrders.ApproveAsync(id, scope, cancellationToken), cancellationToken);
    }

    [HttpPost("{id:int}/lines/{lineId:int}/dispatch")]
    [Authorize(Policy = CapabilityPolicies.Post)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Dispatch(
        int id,
        int lineId,
        [FromBody] DispatchTransferOrderRequest request,
        CancellationToken cancellationToken)
    {
        var dispatchedBy = User.FindFirstValue(ClaimTypes.NameIdentifier) ??
                           User.FindFirstValue("sub") ??
                           User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(dispatchedBy))
            return Unauthorized(ApiResponse<object>.CreateFailure("An authenticated dispatcher identity is required."));

        var order = await transferOrders.GetByIdAsync(id, cancellationToken);
        if (order is null)
            return NotFound(ApiResponse<object>.CreateFailure("Transfer order not found."));
        if (!await authorization.CanAccessTransferAsync(
                User, order.FromLocationId, order.ToLocationId, CompanyCapability.Post))
            return Forbid();
        if (request is null || request.Quantity <= 0)
            return BadRequest(ApiResponse<object>.CreateFailure("Dispatch quantity must be positive."));

        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key is required for dispatch."));
        if (idempotencyKey.Length > 200)
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key must be 200 characters or fewer."));

        var scope = CreateMutationScope(
            order.CompanyId, order.FromLocationId, order.ToLocationId, CompanyCapability.Post);
        TransferDispatchView? result = null;
        await idempotencyKeyStore.ExecuteAsync(
            $"{tenantContext.TenantId}:{Request.Method}:{Request.Path}",
            idempotencyKey,
            IdempotencyRequestHasher.Compute(request),
            async () => result = await transferOrders.DispatchAsync(
                id,
                lineId,
                request.Quantity,
                idempotencyKey,
                dispatchedBy.Trim(),
                scope,
                cancellationToken),
            cancellationToken);

        result ??= await transferOrders.GetDispatchByKeyAsync(
            id, lineId, idempotencyKey, cancellationToken);
        return result is null
            ? StatusCode(StatusCodes.Status500InternalServerError,
                ApiResponse<object>.CreateFailure("The dispatch result could not be recovered."))
            : Ok(ApiResponse<TransferDispatchView>.CreateSuccess(result));
    }

    [HttpPost("{id:int}/lines/{lineId:int}/transit/{transitEntryId:int}/receive")]
    [Authorize(Policy = CapabilityPolicies.Post)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public Task<IActionResult> ReceiveTransit(
        int id,
        int lineId,
        int transitEntryId,
        [FromBody] TransferTransitSettlementRequest request,
        [FromServices] IIdempotencyKeyStore idempotencyKeyStore,
        CancellationToken cancellationToken) =>
        ResolveTransitAsync(
            id,
            lineId,
            transitEntryId,
            request,
            TransferTransitSettlementType.Received,
            idempotencyKeyStore,
            cancellationToken);

    [HttpPost("{id:int}/lines/{lineId:int}/transit/{transitEntryId:int}/quarantine")]
    [Authorize(Policy = CapabilityPolicies.Post)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public Task<IActionResult> QuarantineTransit(
        int id,
        int lineId,
        int transitEntryId,
        [FromBody] TransferTransitSettlementRequest request,
        [FromServices] IIdempotencyKeyStore idempotencyKeyStore,
        CancellationToken cancellationToken) =>
        ResolveTransitAsync(
            id,
            lineId,
            transitEntryId,
            request,
            TransferTransitSettlementType.Quarantined,
            idempotencyKeyStore,
            cancellationToken);

    [HttpPost("{id:int}/lines/{lineId:int}/transit/{transitEntryId:int}/return")]
    [Authorize(Policy = CapabilityPolicies.Post)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public Task<IActionResult> ReturnTransit(
        int id,
        int lineId,
        int transitEntryId,
        [FromBody] TransferTransitSettlementRequest request,
        [FromServices] IIdempotencyKeyStore idempotencyKeyStore,
        CancellationToken cancellationToken) =>
        ResolveTransitAsync(
            id,
            lineId,
            transitEntryId,
            request,
            TransferTransitSettlementType.Returned,
            idempotencyKeyStore,
            cancellationToken);

    [HttpPost("{id:int}/cancel")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Cancel(int id, CancellationToken cancellationToken)
    {
        var order = await transferOrders.GetByIdAsync(id, cancellationToken);
        if (order is null)
            return NotFound(ApiResponse<object>.CreateFailure("Transfer order not found."));
        if (!await authorization.CanAccessTransferAsync(
                User, order.FromLocationId, order.ToLocationId, CompanyCapability.Edit) ||
            !await authorization.CanAccessTransferAsync(
                User, order.FromLocationId, order.ToLocationId, CompanyCapability.Post))
            return Forbid();
        var scope = CreateMutationScope(order.CompanyId, order.FromLocationId, order.ToLocationId, CompanyCapability.Post);
        return await RunMutationAsync(new { id }, scope, () => transferOrders.CancelAsync(id, scope, cancellationToken), cancellationToken);
    }

    private StockMutationScope CreateMutationScope(
        int companyId,
        int fromLocationId,
        int toLocationId,
        CompanyCapability capability) =>
        new(companyId, () => authorization.CanAccessTransferAsync(
            User, fromLocationId, toLocationId, capability));

    private async Task<bool> CanEditTransferAsync(int fromLocationId, int toLocationId, int companyId) =>
        await authorization.CanAccessTransferAsync(User, fromLocationId, toLocationId, CompanyCapability.Edit) &&
        await authorization.GetLocationCompanyIdAsync(User, fromLocationId) == companyId;

    private async Task<IActionResult> ResolveTransitAsync(
        int id,
        int lineId,
        int transitEntryId,
        TransferTransitSettlementRequest request,
        TransferTransitSettlementType settlementType,
        IIdempotencyKeyStore idempotencyKeyStore,
        CancellationToken cancellationToken)
    {
        var settledBy = User.FindFirstValue(ClaimTypes.NameIdentifier) ??
                        User.FindFirstValue("sub") ??
                        User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(settledBy))
            return Unauthorized(ApiResponse<object>.CreateFailure("An authenticated settlement identity is required."));
        if (request is null)
            return BadRequest(ApiResponse<object>.CreateFailure("A settlement request is required."));

        var order = await transferOrders.GetByIdAsync(id, cancellationToken);
        if (order is null)
            return NotFound(ApiResponse<object>.CreateFailure("Transfer order not found."));
        if (!await authorization.CanAccessTransferAsync(
                User, order.FromLocationId, order.ToLocationId, CompanyCapability.Post))
            return Forbid();

        var resolvedRequest = request with { SettlementType = settlementType };
        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key is required for transit settlement."));
        if (idempotencyKey.Length > 200)
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key must be 200 characters or fewer."));

        var scope = CreateMutationScope(
            order.CompanyId, order.FromLocationId, order.ToLocationId, CompanyCapability.Post);
        TransferTransitSettlementView? result = null;
        await idempotencyKeyStore.ExecuteAsync(
            $"{tenantContext.TenantId}:{Request.Method}:{Request.Path}",
            idempotencyKey,
            IdempotencyRequestHasher.Compute(resolvedRequest),
            async () => result = await transferOrders.ResolveTransitAsync(
                id,
                lineId,
                transitEntryId,
                resolvedRequest,
                idempotencyKey,
                settledBy.Trim(),
                scope,
                cancellationToken),
            cancellationToken);

        return result is null
            ? StatusCode(StatusCodes.Status500InternalServerError,
                ApiResponse<object>.CreateFailure("The transit settlement result could not be recovered."))
            : Ok(ApiResponse<TransferTransitSettlementView>.CreateSuccess(result));
    }

    private async Task<IActionResult> RunMutationAsync<T>(
        T request,
        StockMutationScope scope,
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        var key = Request.Headers["Idempotency-Key"].ToString();
        if (key.Length > 200)
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key must be 200 characters or fewer."));
        if (string.IsNullOrWhiteSpace(key))
        {
            await operation();
        }
        else
        {
            await idempotencyKeyStore.ExecuteAsync(
                $"{tenantContext.TenantId}:{Request.Method}:{Request.Path}",
                key,
                IdempotencyRequestHasher.Compute(request),
                operation,
                cancellationToken);
        }
        return NoContent();
    }

    private async Task<IActionResult> AccessFailureAsync()
    {
        return await authorization.IsTenantAdministratorAsync(User)
            ? NotFound(ApiResponse<object>.CreateFailure("Transfer order not found."))
            : Forbid();
    }
}
