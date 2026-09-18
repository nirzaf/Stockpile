using Asp.Versioning;
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
[Route("api/v{version:apiVersion}/stock")]
[Produces("application/json")]
[Authorize(Policy = "Api")]
[EnableRateLimiting("Api")]
public sealed class StockReturnsController(
    IStockService stock,
    ICurrentUserAuthorization authorization) : ControllerBase
{
    [HttpPost("returns")]
    [Authorize(Policy = CapabilityPolicies.Post)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Create(
        [FromBody] CreateStockReturnRequest request,
        [FromServices] IIdempotencyKeyStore idempotencyKeyStore,
        [FromServices] ITenantContext tenantContext)
    {
        var original = await stock.GetTransactionAsync(request.OriginalTransactionId);
        if (original is null)
            return NotFound(ApiResponse<object>.CreateFailure("Original stock transaction not found."));

        var cancellationToken = HttpContext.RequestAborted;
        if (!await authorization.CanAccessLocationAsync(
                User, original.FromLocationId, CompanyCapability.Post, cancellationToken))
        {
            // Returning a type-specific validation result before this check reveals whether
            // a movement exists in a company the caller cannot access.
            return NotFound(ApiResponse<object>.CreateFailure("Original stock transaction not found."));
        }

        if (original.TransactionType != TransactionType.Sell)
            return BadRequest(ApiResponse<object>.CreateFailure("Only sale transactions can be returned."));

        var mutationScope = new StockMutationScope(
            await authorization.GetLocationCompanyIdAsync(User, original.FromLocationId, cancellationToken),
            token => authorization.CanAccessLocationAsync(
                User, original.FromLocationId, CompanyCapability.Post, token));

        return await RunMutationAsync(request, idempotencyKeyStore, tenantContext,
            () => stock.ReturnStockAsync(request, mutationScope));
    }

    private async Task<IActionResult> RunMutationAsync<T>(
        T request,
        IIdempotencyKeyStore idempotencyKeyStore,
        ITenantContext tenantContext,
        Func<Task> operation)
    {
        var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
        if (idempotencyKey.Length > 200)
            return BadRequest(ApiResponse<object>.CreateFailure("Idempotency-Key must be 200 characters or fewer."));

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            await operation();
        }
        else
        {
            var scope = $"{tenantContext.TenantId}:{Request.Method}:{Request.Path}";
            await idempotencyKeyStore.ExecuteAsync(
                scope,
                idempotencyKey,
                IdempotencyRequestHasher.Compute(request),
                operation,
                HttpContext.RequestAborted);
        }

        return NoContent();
    }
}
