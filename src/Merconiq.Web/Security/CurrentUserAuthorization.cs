using System.Security.Claims;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Merconiq.Web.Security;

/// <summary>Checks a circuit principal against current tenant, security-stamp, and role data.</summary>
public sealed class CurrentUserAuthorization(
    UserManager<ApplicationUser> userManager,
    ITenantContext tenantContext,
    IOptions<IdentityOptions> identityOptions)
{
    public async Task<bool> IsSessionCurrentAsync(ClaimsPrincipal principal)
    {
        return await FindCurrentUserAsync(principal) is not null;
    }

    public async Task<bool> CanEditOrganizationAsync(ClaimsPrincipal principal)
    {
        var user = await FindCurrentUserAsync(principal);
        if (user is null)
        {
            return false;
        }

        var roles = await userManager.GetRolesAsync(user);
        return roles.Contains("Admin", StringComparer.Ordinal) ||
               roles.Contains("Manager", StringComparer.Ordinal);
    }

    private async Task<ApplicationUser?> FindCurrentUserAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || !tenantContext.IsResolved)
        {
            return null;
        }

        var principalTenant = principal.FindFirstValue("tenant_id");
        if (!string.Equals(principalTenant, tenantContext.TenantId, StringComparison.Ordinal))
        {
            return null;
        }

        var securityStampClaimType = identityOptions.Value.ClaimsIdentity.SecurityStampClaimType;
        var principalSecurityStamp = principal.FindFirstValue(securityStampClaimType);
        if (string.IsNullOrWhiteSpace(principalSecurityStamp))
        {
            return null;
        }

        var user = await userManager.GetUserAsync(principal);
        if (user is null || !string.Equals(user.TenantId, tenantContext.TenantId, StringComparison.Ordinal))
        {
            return null;
        }

        var currentSecurityStamp = await userManager.GetSecurityStampAsync(user);
        return string.Equals(principalSecurityStamp, currentSecurityStamp, StringComparison.Ordinal)
            ? user
            : null;
    }
}
