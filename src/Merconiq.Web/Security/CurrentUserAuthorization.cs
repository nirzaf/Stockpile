using System.Security.Claims;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Merconiq.Web.Security;

/// <summary>Checks a circuit principal against fresh tenant-scoped Identity data.</summary>
public sealed class CurrentUserAuthorization(
    ITenantContext tenantContext,
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityOptions> identityOptions) : ICurrentUserAuthorization
{
    private const CompanyCapability AllCapabilities =
        CompanyCapability.View | CompanyCapability.Edit | CompanyCapability.Approve |
        CompanyCapability.Post | CompanyCapability.Reverse | CompanyCapability.Administer |
        CompanyCapability.OverrideExpiredStock | CompanyCapability.OverrideQuarantinedStock;

    public Task<bool> IsSessionCurrentAsync(ClaimsPrincipal principal) =>
        WithCurrentUserAsync(principal, false, (_, _, _) => Task.FromResult(true));

    public Task<bool> IsTenantAdministratorAsync(ClaimsPrincipal principal) =>
        WithCurrentUserAsync(principal, false, (_, _, roles) =>
            Task.FromResult(roles.Contains("Admin", StringComparer.Ordinal)));

    public Task<bool> CanEditOrganizationAsync(ClaimsPrincipal principal) =>
        WithCurrentUserAsync(principal, false, (_, _, roles) => Task.FromResult(
            roles.Contains("Admin", StringComparer.Ordinal) ||
            roles.Contains("Manager", StringComparer.Ordinal) ||
            roles.Contains("Buyer", StringComparer.Ordinal) ||
            roles.Contains("CompanyAdmin", StringComparer.Ordinal)));

    public Task<bool> HasCompanyCapabilityAsync(
        ClaimsPrincipal principal,
        CompanyCapability capability) =>
        WithCurrentUserAsync(principal, false, async (db, user, roles) =>
        {
            if (!IsCapabilityValid(capability) || !RoleCanPerform(roles, capability))
            {
                return false;
            }

            if (capability is not (CompanyCapability.OverrideExpiredStock or CompanyCapability.OverrideQuarantinedStock) &&
                roles.Contains("Admin", StringComparer.Ordinal))
            {
                return true;
            }

            return await db.CompanyMemberships.AnyAsync(grant =>
                grant.UserId == user.Id && grant.IsActive &&
                (grant.Capabilities & capability) == capability);
        });

    public Task<bool> CanAccessCompanyAsync(
        ClaimsPrincipal principal,
        int companyId,
        CompanyCapability capability) =>
        WithCurrentUserAsync(principal, false, (db, user, roles) =>
            CanAccessCompanyAsync(db, user, roles, companyId, capability));

    public Task<IReadOnlySet<int>> GetAccessibleCompanyIdsAsync(
        ClaimsPrincipal principal,
        CompanyCapability capability,
        CancellationToken cancellationToken = default) =>
        WithCurrentUserAsync(principal, (IReadOnlySet<int>)new HashSet<int>(), async (db, user, roles, token) =>
        {
            if (!IsCapabilityValid(capability) || !RoleCanPerform(roles, capability))
            {
                return new HashSet<int>();
            }

            if (roles.Contains("Admin", StringComparer.Ordinal) &&
                capability != CompanyCapability.OverrideQuarantinedStock)
            {
                return (IReadOnlySet<int>)new HashSet<int>(
                    await db.Companies.Select(company => company.Id).ToListAsync(token));
            }

            var membership = db.CompanyMemberships
                .Where(grant => grant.UserId == user.Id && grant.IsActive &&
                    (grant.Capabilities & capability) == capability)
                .Select(grant => grant.CompanyId);
            return (IReadOnlySet<int>)new HashSet<int>(
                await membership.ToListAsync(token));
        }, cancellationToken);

    public Task<bool> CanAccessBranchAsync(
        ClaimsPrincipal principal,
        int branchId,
        CompanyCapability capability) =>
        WithCurrentUserAsync(principal, false, async (db, user, roles) =>
        {
            var companyId = await db.Branches
                .Where(branch => branch.Id == branchId)
                .Select(branch => (int?)branch.CompanyId)
                .SingleOrDefaultAsync();
            return companyId.HasValue &&
                   await CanAccessCompanyAsync(db, user, roles, companyId.Value, capability);
        });

    public Task<bool> CanAccessLocationAsync(
        ClaimsPrincipal principal,
        int locationId,
        CompanyCapability capability,
        CancellationToken cancellationToken = default) =>
        WithCurrentUserAsync(principal, false, async (db, user, roles, token) =>
        {
            var companyId = await db.Locations
                .Where(location => location.Id == locationId)
                .Select(location => (int?)location.Branch!.CompanyId)
                .SingleOrDefaultAsync(token);
            if (!companyId.HasValue)
            {
                return roles.Contains("Admin", StringComparer.Ordinal) &&
                       capability != CompanyCapability.OverrideQuarantinedStock &&
                       await db.Locations.AnyAsync(location => location.Id == locationId, token);
            }

            return await CanAccessCompanyAsync(
                db, user, roles, companyId.Value, capability, cancellationToken: token);
        }, cancellationToken);

    public Task<bool> CanOverrideExpiredStockAtLocationAsync(
        ClaimsPrincipal principal,
        int locationId) =>
        WithCurrentUserAsync(principal, false, async (db, user, roles) =>
        {
            var companyId = await db.Locations
                .Where(location => location.Id == locationId)
                .Select(location => (int?)location.Branch!.CompanyId)
                .SingleOrDefaultAsync();
            return companyId.HasValue && await CanAccessCompanyAsync(
                db, user, roles, companyId.Value, CompanyCapability.OverrideExpiredStock,
                requireExplicitGrant: true);
        });

    public Task<bool> CanOverrideQuarantinedStockAtLocationAsync(
        ClaimsPrincipal principal,
        int locationId) =>
        WithCurrentUserAsync(principal, false, async (db, user, roles) =>
        {
            var companyId = await db.Locations
                .Where(location => location.Id == locationId)
                .Select(location => (int?)location.Branch!.CompanyId)
                .SingleOrDefaultAsync();
            return companyId.HasValue && await CanAccessCompanyAsync(
                db, user, roles, companyId.Value, CompanyCapability.OverrideQuarantinedStock,
                requireExplicitGrant: true);
        });

    public Task<int?> GetLocationCompanyIdAsync(ClaimsPrincipal principal, int locationId) =>
        WithCurrentUserAsync(principal, (int?)null, async (db, _, _) =>
            await db.Locations
                .Where(location => location.Id == locationId)
                .Select(location => (int?)location.Branch!.CompanyId)
                .SingleOrDefaultAsync());

    public Task<bool> CanAccessTransferAsync(
        ClaimsPrincipal principal,
        int fromLocationId,
        int toLocationId,
        CompanyCapability capability,
        CancellationToken cancellationToken = default) =>
        WithCurrentUserAsync(principal, false, async (db, user, roles, token) =>
        {
            var locationIds = new[] { fromLocationId, toLocationId }.Distinct().ToArray();
            var companyIds = await db.Locations
                .Where(location => locationIds.Contains(location.Id) && location.BranchId.HasValue)
                .Select(location => location.Branch!.CompanyId)
                .ToListAsync(token);
            return companyIds.Count == locationIds.Length && companyIds.Distinct().Count() == 1 &&
                   await CanAccessCompanyAsync(
                       db, user, roles, companyIds[0], capability, cancellationToken: token);
        }, cancellationToken);

    public Task<bool> CanAssignLocationBranchAsync(
        ClaimsPrincipal principal,
        int locationId,
        int? targetBranchId) =>
        WithCurrentUserAsync(principal, false, async (db, user, roles) =>
        {
            var location = await db.Locations
                .Include(existing => existing.Branch)
                .SingleOrDefaultAsync(existing => existing.Id == locationId);
            if (location is null)
            {
                return false;
            }

            var oldCompanyId = location.Branch?.CompanyId;

            if (!oldCompanyId.HasValue && !roles.Contains("Admin", StringComparer.Ordinal))
            {
                return false;
            }

            var targetBranch = targetBranchId.HasValue
                ? await db.Branches
                    .Where(branch => branch.Id == targetBranchId.Value)
                    .Select(branch => new
                    {
                        branch.Id,
                        branch.CompanyId,
                        branch.IsActive,
                        CompanyIsActive = branch.Company.IsActive
                    })
                    .SingleOrDefaultAsync()
                : null;

            if (targetBranchId.HasValue && targetBranch is null)
            {
                return false;
            }

            if (oldCompanyId.HasValue &&
                !await CanAccessCompanyAsync(db, user, roles, oldCompanyId.Value, CompanyCapability.Administer))
            {
                return false;
            }

            if (targetBranch is null)
            {
                return roles.Contains("Admin", StringComparer.Ordinal);
            }

            // Existing locations may remain on an inactive branch, but reassignment
            // and creation must target an active branch in an active company.
            var isExistingAssignment = location.BranchId == targetBranch.Id;
            if (!isExistingAssignment && (!targetBranch.IsActive || !targetBranch.CompanyIsActive))
            {
                return false;
            }

            return await CanAccessCompanyAsync(
                db, user, roles, targetBranch.CompanyId, CompanyCapability.Administer);
        });

    public Task<bool> CanCreateLocationInBranchAsync(ClaimsPrincipal principal, int branchId) =>
        WithCurrentUserAsync(principal, false, async (db, user, roles) =>
        {
            var branch = await db.Branches
                .Where(item => item.Id == branchId)
                .Select(item => new
                {
                    item.CompanyId,
                    item.IsActive,
                    CompanyIsActive = item.Company.IsActive
                })
                .SingleOrDefaultAsync();
            return branch is not null && branch.IsActive && branch.CompanyIsActive &&
                   await CanAccessCompanyAsync(
                       db, user, roles, branch.CompanyId, CompanyCapability.Administer);
        });

    private async Task<TResult> WithCurrentUserAsync<TResult>(
        ClaimsPrincipal principal,
        TResult unauthenticatedResult,
        Func<InventoryDbContext, ApplicationUser, IReadOnlyCollection<string>, CancellationToken, Task<TResult>> authorize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (principal.Identity?.IsAuthenticated != true || !tenantContext.IsResolved)
        {
            return unauthenticatedResult;
        }

        var expectedTenantId = tenantContext.TenantId;
        var principalTenant = principal.FindFirstValue("tenant_id");
        if (!string.Equals(principalTenant, expectedTenantId, StringComparison.Ordinal))
        {
            return unauthenticatedResult;
        }

        var claimsIdentity = identityOptions.Value.ClaimsIdentity;
        var principalSecurityStamp = principal.FindFirstValue(claimsIdentity.SecurityStampClaimType);
        var principalUserId = principal.FindFirstValue(claimsIdentity.UserIdClaimType);
        if (string.IsNullOrWhiteSpace(principalSecurityStamp) || string.IsNullOrWhiteSpace(principalUserId))
        {
            return unauthenticatedResult;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var scopedTenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        if (!scopedTenantContext.IsResolved)
        {
            scopedTenantContext.SetTenant(expectedTenantId);
        }
        else if (!string.Equals(scopedTenantContext.TenantId, expectedTenantId, StringComparison.Ordinal))
        {
            return unauthenticatedResult;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var user = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == principalUserId, cancellationToken);
        if (user is null || !string.Equals(user.TenantId, expectedTenantId, StringComparison.Ordinal) ||
            !string.Equals(principalSecurityStamp, user.SecurityStamp, StringComparison.Ordinal))
        {
            return unauthenticatedResult;
        }

        var roles = await db.UserRoles
            .Where(userRole => userRole.UserId == user.Id)
            .Join(db.Roles, userRole => userRole.RoleId, role => role.Id, (_, role) => role.Name)
            .Where(roleName => roleName != null)
            .Select(roleName => roleName!)
            .ToArrayAsync(cancellationToken);

        return await authorize(db, user, roles, cancellationToken);
    }

    private async Task<TResult> WithCurrentUserAsync<TResult>(
        ClaimsPrincipal principal,
        TResult unauthenticatedResult,
        Func<InventoryDbContext, ApplicationUser, IReadOnlyCollection<string>, Task<TResult>> authorize)
    {
        if (principal.Identity?.IsAuthenticated != true || !tenantContext.IsResolved)
        {
            return unauthenticatedResult;
        }

        var expectedTenantId = tenantContext.TenantId;
        var principalTenant = principal.FindFirstValue("tenant_id");
        if (!string.Equals(principalTenant, expectedTenantId, StringComparison.Ordinal))
        {
            return unauthenticatedResult;
        }

        var securityStampClaimType = identityOptions.Value.ClaimsIdentity.SecurityStampClaimType;
        var principalSecurityStamp = principal.FindFirstValue(securityStampClaimType);
        if (string.IsNullOrWhiteSpace(principalSecurityStamp))
        {
            return unauthenticatedResult;
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
            return unauthenticatedResult;
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.GetUserAsync(principal);
        if (user is null || !string.Equals(user.TenantId, expectedTenantId, StringComparison.Ordinal))
        {
            return unauthenticatedResult;
        }

        var currentSecurityStamp = await userManager.GetSecurityStampAsync(user);
        if (!string.Equals(principalSecurityStamp, currentSecurityStamp, StringComparison.Ordinal))
        {
            return unauthenticatedResult;
        }

        var roles = await userManager.GetRolesAsync(user);
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        return await authorize(db, user, roles.ToArray());
    }

    private static async Task<bool> CanAccessCompanyAsync(
        InventoryDbContext db,
        ApplicationUser user,
        IReadOnlyCollection<string> roles,
        int companyId,
        CompanyCapability capability,
        bool requireExplicitGrant = false,
        CancellationToken cancellationToken = default)
    {
        if (companyId <= 0 || !IsCapabilityValid(capability) || !RoleCanPerform(roles, capability) ||
            !await db.Companies.AnyAsync(company => company.Id == companyId, cancellationToken))
        {
            return false;
        }

        if (!requireExplicitGrant && capability != CompanyCapability.OverrideQuarantinedStock &&
            roles.Contains("Admin", StringComparer.Ordinal))
        {
            return true;
        }

        return await db.CompanyMemberships.AnyAsync(grant =>
            grant.CompanyId == companyId &&
            grant.UserId == user.Id &&
            grant.IsActive &&
            (grant.Capabilities & capability) == capability,
            cancellationToken);
    }

    private static bool IsCapabilityValid(CompanyCapability capability) =>
        capability != CompanyCapability.None && (capability & ~AllCapabilities) == 0;

    private static bool RoleCanPerform(IReadOnlyCollection<string> roles, CompanyCapability capability) =>
        capability switch
        {
            CompanyCapability.View => roles.Count > 0,
            CompanyCapability.Edit => roles.Any(role => role is "Admin" or "Manager" or "Buyer" or "CompanyAdmin"),
            CompanyCapability.Approve => roles.Any(role => role is "Admin" or "Accountant"),
            CompanyCapability.Post => roles.Any(role => role is "Admin" or "Manager" or "Staff" or "Operator" or "Accountant" or "Cashier"),
            CompanyCapability.Reverse => roles.Any(role => role is "Admin" or "Accountant"),
            CompanyCapability.OverrideExpiredStock => roles.Any(role => role is "Admin" or "Accountant"),
            CompanyCapability.OverrideQuarantinedStock => roles.Any(role => role is "Admin" or "Accountant"),
            CompanyCapability.Administer => roles.Any(role => role is "Admin" or "CompanyAdmin"),
            _ => false
        };
}
