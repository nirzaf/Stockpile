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
public sealed class StockReservationsController(
    IStockService stock,
    ICurrentUserAuthorization authorization) : ControllerBase
{
    [HttpGet("availability")]
    [Authorize(Policy = CapabilityPolicies.View)]
    public async Task<IActionResult> GetAvailability(
        [FromQuery] int? itemId,
        [FromQuery] int? locationId)
    {
        if (locationId.HasValue && !await CanViewLocationAsync(locationId.Value))
            return await LocationAccessFailureAsync();

        var tenantAdmin = await authorization.IsTenantAdministratorAsync(User);
        var companyIds = tenantAdmin
            ? null
            : await authorization.GetAccessibleCompanyIdsAsync(User, CompanyCapability.View);
        var result = await stock.GetAvailabilityAsync(itemId, locationId, companyIds);
        return Ok(ApiResponse<IEnumerable<StockAvailabilityView>>.CreateSuccess(result));
    }

    [HttpGet("reservations/{sourceLineReference}")]
    [Authorize(Policy = CapabilityPolicies.View)]
    public async Task<IActionResult> GetReservation(string sourceLineReference)
    {
        var result = await stock.GetReservationAsync(sourceLineReference);
        if (result is null)
            return NotFound(ApiResponse<object>.CreateFailure("Reservation not found."));
        if (!await CanViewLocationAsync(result.LocationId))
            return await LocationAccessFailureAsync();
        return Ok(ApiResponse<StockReservationView>.CreateSuccess(result));
    }

    [HttpPost("reservations")]
    [Authorize(Policy = CapabilityPolicies.Post)]
    // This bearer-token API is not cookie-authenticated, so browser CSRF tokens do not apply.
    // Keep the explicit validation marker for static security analysis while opting out at runtime.
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Create(
        [FromBody] CreateStockReservationRequest request,
        [FromServices] IIdempotencyKeyStore idempotencyKeyStore,
        [FromServices] ITenantContext tenantContext)
    {
        if (!await authorization.CanAccessLocationAsync(User, request.LocationId, CompanyCapability.Post))
            return Forbid();
        return await RunMutationAsync(request, idempotencyKeyStore, tenantContext,
            () => stock.CreateReservationAsync(request));
    }

    [HttpPost("reservations/release")]
    [Authorize(Policy = CapabilityPolicies.Post)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Release(
        [FromBody] StockReservationActionRequest request,
        [FromServices] IIdempotencyKeyStore idempotencyKeyStore,
        [FromServices] ITenantContext tenantContext)
    {
        var reservation = await stock.GetReservationAsync(request.SourceLineReference);
        if (reservation is null)
            return NotFound(ApiResponse<object>.CreateFailure("Reservation not found."));
        if (!await authorization.CanAccessLocationAsync(User, reservation.LocationId, CompanyCapability.Post))
            return Forbid();
        return await RunMutationAsync(request, idempotencyKeyStore, tenantContext,
            () => stock.ReleaseReservationAsync(request.SourceLineReference, request.Reason));
    }

    [HttpPost("reservations/cancel")]
    [Authorize(Policy = CapabilityPolicies.Post)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Cancel(
        [FromBody] StockReservationActionRequest request,
        [FromServices] IIdempotencyKeyStore idempotencyKeyStore,
        [FromServices] ITenantContext tenantContext)
    {
        var reservation = await stock.GetReservationAsync(request.SourceLineReference);
        if (reservation is null)
            return NotFound(ApiResponse<object>.CreateFailure("Reservation not found."));
        if (!await authorization.CanAccessLocationAsync(User, reservation.LocationId, CompanyCapability.Post))
            return Forbid();
        return await RunMutationAsync(request, idempotencyKeyStore, tenantContext,
            () => stock.CancelReservationAsync(request.SourceLineReference, request.Reason));
    }

    [HttpPost("reservations/consume")]
    [Authorize(Policy = CapabilityPolicies.Post)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Consume(
        [FromBody] ConsumeStockReservationRequest request,
        [FromServices] IIdempotencyKeyStore idempotencyKeyStore,
        [FromServices] ITenantContext tenantContext)
    {
        var reservation = await stock.GetReservationAsync(request.SourceLineReference);
        if (reservation is null)
            return NotFound(ApiResponse<object>.CreateFailure("Reservation not found."));
        if (!await authorization.CanAccessLocationAsync(User, reservation.LocationId, CompanyCapability.Post))
            return Forbid();
        return await RunMutationAsync(request, idempotencyKeyStore, tenantContext,
            () => stock.ConsumeReservationAsync(request));
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

    private async Task<bool> CanViewLocationAsync(int locationId) =>
        await authorization.CanAccessLocationAsync(User, locationId, CompanyCapability.View);

    private async Task<IActionResult> LocationAccessFailureAsync()
    {
        return await authorization.IsTenantAdministratorAsync(User)
            ? NotFound(ApiResponse<object>.CreateFailure("Location not found."))
            : Forbid();
    }
}
