using System.Globalization;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Web.Controllers.Api.V1;

/// <summary>
/// Applies company scope before API model validation can return a body-validation response.
/// The endpoint's [Authorize] policies remain the outer authentication/capability boundary.
/// </summary>
public sealed class PurchaseOrderProgressCompanyScopeFilter(
    InventoryDbContext db,
    ICurrentUserAuthorization authorization) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!TryGetRouteId(context, "companyId", out var companyId) ||
            !TryGetRouteId(context, "purchaseOrderId", out var purchaseOrderId))
        {
            context.Result = new NotFoundResult();
            return;
        }

        if (!await authorization.CanAccessCompanyAsync(
                context.HttpContext.User, companyId, CompanyCapability.Post))
        {
            context.Result = new ForbidResult();
            return;
        }

        var mappedCompanyId = await db.PurchaseOrders
            .Where(order => order.Id == purchaseOrderId)
            .Select(order => order.DocumentIdentity.CompanyId)
            .SingleOrDefaultAsync(context.HttpContext.RequestAborted);
        if (mappedCompanyId != companyId)
        {
            context.Result = new NotFoundObjectResult(
                ApiResponse<object>.CreateFailure("Purchase order not found in this company."));
        }
    }

    private static bool TryGetRouteId(
        AuthorizationFilterContext context,
        string routeKey,
        out int id)
    {
        var routeValue = context.RouteData.Values.GetValueOrDefault(routeKey);
        return int.TryParse(
            Convert.ToString(routeValue, CultureInfo.InvariantCulture),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out id);
    }
}
