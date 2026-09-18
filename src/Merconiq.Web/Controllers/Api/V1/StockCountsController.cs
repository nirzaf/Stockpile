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
[Route("api/v{version:apiVersion}/stock/counts")]
[Produces("application/json")]
[Authorize(Policy = "Api")]
[EnableRateLimiting("Api")]
public sealed class StockCountsController(
    IStockCountService stockCounts,
    ICurrentUserAuthorization authorization) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = CapabilityPolicies.Post)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    [ProducesResponseType(typeof(ApiResponse<StockCountView>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Start(
        [FromBody] StartStockCountRequest request,
        CancellationToken cancellationToken)
    {
        if (!await authorization.CanAccessLocationAsync(
                User, request.LocationId, CompanyCapability.Post, cancellationToken))
        {
            return Forbid();
        }

        var scope = new StockMutationScope(
            await authorization.GetLocationCompanyIdAsync(User, request.LocationId),
            token => authorization.CanAccessLocationAsync(
                User, request.LocationId, CompanyCapability.Post, token));
        var count = await stockCounts.StartAsync(request.LocationId, scope, cancellationToken);

        return CreatedAtRoute(
            "GetStockCountById",
            new { version = "1.0", countId = count.Id },
            ApiResponse<StockCountView>.CreateSuccess(count));
    }

    [HttpGet("{countId:int}", Name = "GetStockCountById")]
    [Authorize(Policy = CapabilityPolicies.View)]
    [ProducesResponseType(typeof(ApiResponse<StockCountView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(int countId, CancellationToken cancellationToken)
    {
        var isTenantAdministrator = await authorization.IsTenantAdministratorAsync(User);
        var companyIds = isTenantAdministrator
            ? null
            : await authorization.GetAccessibleCompanyIdsAsync(User, CompanyCapability.View);
        var count = await stockCounts.GetAsync(countId, companyIds, cancellationToken);

        return count is null
            ? NotFound(ApiResponse<object>.CreateFailure("Stock count not found."))
            : Ok(ApiResponse<StockCountView>.CreateSuccess(count));
    }

    [HttpPost("{countId:int}/lines/{lineId:int}/observations")]
    [Authorize(Policy = CapabilityPolicies.Post)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    [ProducesResponseType(typeof(ApiResponse<StockCountLineView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RecordObservation(
        int countId,
        int lineId,
        [FromBody] RecordStockCountObservationRequest request,
        CancellationToken cancellationToken)
    {
        var access = await stockCounts.GetAuthorizationContextAsync(countId, cancellationToken);
        if (access is null)
            return NotFound(ApiResponse<object>.CreateFailure("Stock count not found."));

        if (!await authorization.CanAccessLocationAsync(
                User, access.LocationId, CompanyCapability.Post, cancellationToken))
        {
            return Forbid();
        }

        var scope = new StockMutationScope(
            access.CompanyId,
            token => authorization.CanAccessLocationAsync(
                User, access.LocationId, CompanyCapability.Post, token));
        var line = await stockCounts.RecordObservationAsync(
            countId,
            lineId,
            request.CountedQuantity,
            scope,
            cancellationToken);

        return line is null
            ? NotFound(ApiResponse<object>.CreateFailure("Stock-count line not found."))
            : Ok(ApiResponse<StockCountLineView>.CreateSuccess(line));
    }
}
