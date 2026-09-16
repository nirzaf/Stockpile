using Merconiq.Core.Entities;
using Merconiq.Core.Models;

namespace Merconiq.Core.Interfaces;

/// <summary>Tenant-safe company, branch, and existing-location ownership workflows.</summary>
public interface IOrganizationService
{
    Task<IReadOnlyList<Company>> GetCompaniesAsync(
        string? search = null,
        IReadOnlyCollection<int>? accessibleCompanyIds = null);
    Task<Company?> GetCompanyAsync(int id);
    Task<Company> CreateCompanyAsync(CreateCompanyRequest request);
    Task UpdateCompanyAsync(int id, UpdateCompanyRequest request);

    Task<IReadOnlyList<Branch>> GetBranchesAsync(int companyId, string? search = null);
    Task<Branch?> GetBranchAsync(int id);
    Task<Branch> CreateBranchAsync(CreateBranchRequest request);
    Task UpdateBranchAsync(int id, UpdateBranchRequest request);

    Task AssignLocationBranchAsync(int locationId, int branchId);
}
