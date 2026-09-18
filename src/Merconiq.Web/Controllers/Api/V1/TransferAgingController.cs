using Asp.Versioning;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Merconiq.Web.Controllers.Api.V1;

/// <summary>Read-only transfer aging and persisted quantity/value conservation evidence.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/transfer-orders/aging")]
[Produces("application/json")]
[Authorize(Policy = "Api")]
[EnableRateLimiting("Api")]
public sealed class TransferAgingController(
    ITransferAgingReconciliationService reconciliation,
    ICurrentUserAuthorization authorization) : ControllerBase
{
    /// <summary>Reads a bounded page of company-authorized transfer-order lines, including closed order states.</summary>
    /// <remarks>
    /// Transit age is whole elapsed days from the oldest still-outstanding dispatch. Conservation compares
    /// immutable dispatch/settlement rows, while separate variances compare those rows to stock transaction
    /// and valuation entries. Branch responsibility and write-offs are not inferred by this report.
    /// </remarks>
    [HttpGet]
    [Authorize(Policy = CapabilityPolicies.View)]
    [ProducesResponseType(typeof(ApiResponse<TransferAgingReconciliationPage>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(
        [FromQuery] int? companyId,
        [FromQuery] int? afterLineId,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (companyId is <= 0 || afterLineId is <= 0 || pageSize is < 1 or > 100)
            return BadRequest(ApiResponse<object>.CreateFailure(
                "Company and cursor identifiers must be positive, and pageSize must be between 1 and 100."));

        var companyIds = await authorization.GetAccessibleCompanyIdsAsync(
            User, CompanyCapability.View, cancellationToken);
        if (companyId.HasValue)
        {
            if (!companyIds.Contains(companyId.Value))
                return Forbid();
            companyIds = new HashSet<int> { companyId.Value };
        }

        var page = await reconciliation.GetPageAsync(
            companyIds, afterLineId, pageSize, cancellationToken);
        return Ok(ApiResponse<TransferAgingReconciliationPage>.CreateSuccess(page));
    }
}
