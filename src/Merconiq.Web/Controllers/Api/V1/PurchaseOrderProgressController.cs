using System.Security.Claims;
using Asp.Versioning;
using Merconiq.Core.Entities;
using Merconiq.Core.Exceptions;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Web.Controllers.Api.V1;

/// <summary>Company-authorized PO line obligations, separate from goods-receipt and stock posting.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/companies/{companyId:int}/purchase-orders")]
[Produces("application/json")]
[Authorize(Policy = "Api")]
[EnableRateLimiting("Api")]
public sealed class PurchaseOrderProgressController(
    InventoryDbContext db,
    IPurchaseOrderService purchaseOrders,
    ICurrentUserAuthorization authorization,
    IIdempotencyKeyStore idempotencyKeyStore,
    ITenantContext tenantContext) : ControllerBase
{
    /// <summary>Gets authorized lifecycle and line-obligation progress for a company-mapped PO.</summary>
    [HttpGet("{purchaseOrderId:int}/progress")]
    [Authorize(Policy = CapabilityPolicies.View)]
    public async Task<IActionResult> GetProgress(
        int companyId,
        int purchaseOrderId,
        CancellationToken cancellationToken)
    {
        var scopeFailure = await ValidateCompanyScopeAsync(
            companyId, purchaseOrderId, CompanyCapability.View, cancellationToken);
        if (scopeFailure is not null)
            return scopeFailure;

        var progress = await purchaseOrders.GetReceivingProgressAsync(purchaseOrderId, cancellationToken);
        return progress is null
            ? NotFound(ApiResponse<object>.CreateFailure("Purchase order not found."))
            : Ok(ApiResponse<PurchaseOrderReceivingProgress>.CreateSuccess(progress));
    }

    /// <summary>
    /// Records line quantities without creating a goods receipt or posting stock or GRNI.
    /// Quantities are deltas; reuse the idempotency key for retries of the same request.
    /// </summary>
    [HttpPost("{purchaseOrderId:int}/lines/{lineId:int}/progress")]
    [Authorize(Policy = CapabilityPolicies.Post)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> RecordLineProgress(
        int companyId,
        int purchaseOrderId,
        int lineId,
        [FromBody] PurchaseOrderLineProgressChange request,
        CancellationToken cancellationToken)
    {
        var scopeFailure = await ValidateCompanyScopeAsync(
            companyId, purchaseOrderId, CompanyCapability.Post, cancellationToken);
        if (scopeFailure is not null)
            return scopeFailure;

        if (request is null || request.ReceivedQuantity < 0 ||
            request.AcceptedQuantity < 0 || request.RejectedQuantity < 0 ||
            (request.ReceivedQuantity == 0 && request.AcceptedQuantity == 0 && request.RejectedQuantity == 0))
        {
            return BadRequest(ApiResponse<object>.CreateFailure(
                "At least one non-negative receiving outcome quantity must be positive."));
        }

        var lineBelongsToOrder = await db.OrderDetails.AnyAsync(
            line => line.Id == lineId && line.PurchaseOrderId == purchaseOrderId,
            cancellationToken);
        if (!lineBelongsToOrder)
            return NotFound(ApiResponse<object>.CreateFailure("Purchase-order line not found."));

        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key is required."));
        if (idempotencyKey.Length > 200)
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key must be 200 characters or fewer."));

        var actorId = User.FindFirstValue(ClaimTypes.NameIdentifier) ??
                      User.FindFirstValue("sub") ??
                      User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actorId))
            return Unauthorized(ApiResponse<object>.CreateFailure("An authenticated operator identity is required."));

        var idempotencyScope = $"{tenantContext.TenantId}:{Request.Method}:{Request.Path}";
        try
        {
            await idempotencyKeyStore.ExecuteAsync(
                idempotencyScope,
                idempotencyKey,
                IdempotencyRequestHasher.Compute(request),
                () => purchaseOrders.RecordLineProgressAsync(
                    purchaseOrderId,
                    lineId,
                    request,
                    new PurchaseOrderStatusActor(actorId.Trim(), User.Identity?.Name),
                    cancellationToken),
                cancellationToken);
        }
        catch (ConcurrencyException)
        {
            return Conflict(ApiResponse<object>.CreateFailure(
                "The purchase order changed while line progress was being recorded. Reload and retry with a new key."));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(ApiResponse<object>.CreateFailure(
                "The purchase order changed while line progress was being recorded. Reload and retry with a new key."));
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(ApiResponse<object>.CreateFailure(exception.Message));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(ApiResponse<object>.CreateFailure(exception.Message));
        }

        var progress = await purchaseOrders.GetReceivingProgressAsync(purchaseOrderId, cancellationToken);
        return progress is null
            ? NotFound(ApiResponse<object>.CreateFailure("Purchase order not found."))
            : Ok(ApiResponse<PurchaseOrderReceivingProgress>.CreateSuccess(progress));
    }

    private async Task<IActionResult?> ValidateCompanyScopeAsync(
        int companyId,
        int purchaseOrderId,
        CompanyCapability capability,
        CancellationToken cancellationToken)
    {
        if (!await authorization.CanAccessCompanyAsync(User, companyId, capability))
            return Forbid();

        var mappedCompanyId = await db.PurchaseOrders
            .Where(order => order.Id == purchaseOrderId)
            .Select(order => order.DocumentIdentity.CompanyId)
            .SingleOrDefaultAsync(cancellationToken);
        return mappedCompanyId == companyId
            ? null
            : NotFound(ApiResponse<object>.CreateFailure("Purchase order not found in this company."));
    }
}
