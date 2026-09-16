using Merconiq.Core.Entities;

namespace Merconiq.Core.Interfaces;

/// <summary>Tenant-safe administration of company-scoped user permissions.</summary>
public interface ICompanyMembershipService
{
    Task<IReadOnlyList<CompanyMembership>> GetCompanyMembershipsAsync(int companyId);
    Task SetCapabilitiesAsync(int companyId, string userId, CompanyCapability capabilities);
    Task RemoveMembershipAsync(int companyId, string userId);
}
