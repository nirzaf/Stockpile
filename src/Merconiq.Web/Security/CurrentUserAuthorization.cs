using System.Security.Claims;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Merconiq.Web.Security;

/// <summary>Checks a circuit principal against fresh tenant-scoped Identity data.</summary>
public sealed class CurrentUserAuthorization(
    ITenantContext tenantContext,
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityOptions> identityOptions)
{
    public Task<bool> IsSessionCurrentAsync(ClaimsPrincipal principal) =>
        ValidateCurrentUserAsync(principal, requireOrganizationEdit: false);

    public Task<bool> CanEditOrganizationAsync(ClaimsPrincipal principal) =>
        ValidateCurrentUserAsync(principal, requireOrganizationEdit: true);

    private async Task<bool> ValidateCurrentUserAsync(
        ClaimsPrincipal principal,
        bool requireOrganizationEdit)
    {
        if (principal.Identity?.IsAuthenticated != true || !tenantContext.IsResolved)
        {
            return false;
        }

        var expectedTenantId = tenantContext.TenantId;
        var principalTenant = principal.FindFirstValue("tenant_id");
        if (!string.Equals(principalTenant, expectedTenantId, StringComparison.Ordinal))
        {
            return false;
        }

        var securityStampClaimType = identityOptions.Value.ClaimsIdentity.SecurityStampClaimType;
        var principalSecurityStamp = principal.FindFirstValue(securityStampClaimType);
        if (string.IsNullOrWhiteSpace(principalSecurityStamp))
        {
            return false;
        }

        // A Blazor circuit keeps its scoped services for its lifetime. Query through a
        // new scope on every authorization check so Identity cannot reuse a tracked user.
        await using var scope = scopeFactory.CreateAsyncScope();
        var scopedTenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        if (!scopedTenantContext.IsResolved)
        {
            scopedTenantContext.SetTenant(expectedTenantId);
        }
        else if (!string.Equals(scopedTenantContext.TenantId, expectedTenantId, StringComparison.Ordinal))
        {
            return false;
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.GetUserAsync(principal);
        if (user is null || !string.Equals(user.TenantId, expectedTenantId, StringComparison.Ordinal))
        {
            return false;
        }

        var currentSecurityStamp = await userManager.GetSecurityStampAsync(user);
        if (!string.Equals(principalSecurityStamp, currentSecurityStamp, StringComparison.Ordinal))
        {
            return false;
        }

        if (!requireOrganizationEdit)
        {
            return true;
        }

        var roles = await userManager.GetRolesAsync(user);
        return roles.Contains("Admin", StringComparer.Ordinal) ||
               roles.Contains("Manager", StringComparer.Ordinal);
    }
}
