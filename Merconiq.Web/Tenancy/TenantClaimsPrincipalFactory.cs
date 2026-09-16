using System.Security.Claims;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Merconiq.Web.Tenancy;

/// <summary>
/// Adds the resolved tenant to Identity cookie principals after verifying the user belongs to it.
/// </summary>
public sealed class TenantClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> identityOptions,
    ITenantContext tenantContext)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, identityOptions)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        if (!tenantContext.IsResolved ||
            !string.Equals(user.TenantId, tenantContext.TenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The user does not belong to the resolved tenant.");
        }

        var identity = await base.GenerateClaimsAsync(user);
        foreach (var claim in identity.FindAll("tenant_id").ToList())
        {
            identity.RemoveClaim(claim);
        }

        identity.AddClaim(new Claim("tenant_id", tenantContext.TenantId));
        return identity;
    }
}
