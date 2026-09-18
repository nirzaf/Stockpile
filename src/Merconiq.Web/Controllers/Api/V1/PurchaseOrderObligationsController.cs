using Asp.Versioning;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Web.Controllers.Api.V1;

/// <summary>Read-only, company-authorized ordered-line obligations.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/companies/{companyId:int}/purchase-orders")]
[Produces("application/json")]
[Authorize(Policy = "Api")]
[EnableRateLimiting("Api")]
public sealed class PurchaseOrderObligationsController(
    InventoryDbContext db,
    IPurchaseOrderService purchaseOrders,
    ICurrentUserAuthorization authorization) : ControllerBase
{
    /// <summary>
    /// Gets ordered quantities and conservative outstanding obligations for a company-mapped PO.
    /// No receipt or acceptance progress is inferred in this slice.
    /// </summary>
    [HttpGet("{purchaseOrderId:int}/obligations")]
    [Authorize(Policy = CapabilityPolicies.View)]
    public async Task<IActionResult> GetObligations(
        int companyId,
        int purchaseOrderId,
        CancellationToken cancellationToken)
    {
        if (!await authorization.CanAccessCompanyAsync(User, companyId, CompanyCapability.View))
            return Forbid();

        var mappedCompanyId = await db.PurchaseOrders
            .Where(order => order.Id == purchaseOrderId)
            .Select(order => order.DocumentIdentity.CompanyId)
            .SingleOrDefaultAsync(cancellationToken);
        if (mappedCompanyId != companyId)
            return NotFound(ApiResponse<object>.CreateFailure("Purchase order not found in this company."));

        var obligations = await purchaseOrders.GetLineObligationsAsync(purchaseOrderId, cancellationToken);
        return obligations is null
            ? NotFound(ApiResponse<object>.CreateFailure("Purchase order not found."))
            : Ok(ApiResponse<PurchaseOrderLineObligations>.CreateSuccess(obligations));
    }
}
