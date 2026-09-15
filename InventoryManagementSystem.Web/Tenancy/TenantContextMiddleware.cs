using Microsoft.AspNetCore.Http;

namespace InventoryManagementSystem.Web.Tenancy;

/// <summary>
/// Resolves the tenant before authentication and any Identity or EF-backed endpoint executes.
/// </summary>
public sealed class TenantContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext httpContext, TenantContext tenantContext, HostTenantResolver resolver)
    {
        var tenantId = resolver.Resolve(httpContext.Request.Host.Host);
        if (tenantId is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                title = "Tenant could not be resolved.",
                detail = "The request host is not bound to a configured tenant.",
                status = StatusCodes.Status400BadRequest
            });
            return;
        }

        tenantContext.SetTenant(tenantId);
        await next(httpContext);
    }
}

public static class TenantContextMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<TenantContextMiddleware>();
    }
}
