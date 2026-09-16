using System.Security.Claims;
using Merconiq.Core.Entities;

namespace Merconiq.Web.Security;

/// <summary>Fresh tenant, session, role, and company-capability checks for a principal.</summary>
public interface ICurrentUserAuthorization
{
    Task<bool> IsSessionCurrentAsync(ClaimsPrincipal principal);
    Task<bool> IsTenantAdministratorAsync(ClaimsPrincipal principal);
    Task<bool> CanEditOrganizationAsync(ClaimsPrincipal principal);
    Task<bool> HasCompanyCapabilityAsync(ClaimsPrincipal principal, CompanyCapability capability);
    Task<bool> CanAccessCompanyAsync(ClaimsPrincipal principal, int companyId, CompanyCapability capability);
    Task<IReadOnlySet<int>> GetAccessibleCompanyIdsAsync(ClaimsPrincipal principal, CompanyCapability capability);
    Task<bool> CanAccessBranchAsync(ClaimsPrincipal principal, int branchId, CompanyCapability capability);
    Task<bool> CanAccessLocationAsync(ClaimsPrincipal principal, int locationId, CompanyCapability capability);
    Task<bool> CanAccessTransferAsync(
        ClaimsPrincipal principal,
        int fromLocationId,
        int toLocationId,
        CompanyCapability capability);
    Task<bool> CanAssignLocationBranchAsync(ClaimsPrincipal principal, int locationId, int? targetBranchId);
    Task<bool> CanCreateLocationInBranchAsync(ClaimsPrincipal principal, int branchId);
}
