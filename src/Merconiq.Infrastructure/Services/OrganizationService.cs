using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Infrastructure.Services;

/// <summary>Enforces tenant and company boundaries before organization writes.</summary>
public sealed class OrganizationService(
    IRepository<Company> companies,
    IRepository<Branch> branches,
    IRepository<Location> locations,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext) : IOrganizationService
{
    public async Task<IReadOnlyList<Company>> GetCompaniesAsync(string? search = null)
    {
        EnsureTenantResolved();
        IQueryable<Company> query = companies.Query().OrderBy(c => c.Code);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(c => c.Code.Contains(term) || c.LegalName.Contains(term)).OrderBy(c => c.Code);
        }
        return await query.ToListAsync();
    }

    public Task<Company?> GetCompanyAsync(int id)
    {
        EnsureTenantResolved();
        return GetTenantCompanyAsync(id);
    }

    public async Task<Company> CreateCompanyAsync(CreateCompanyRequest request)
    {
        EnsureTenantResolved();
        var code = NormalizeRequired(request.Code, "Company code", 32);
        var legalName = NormalizeRequired(request.LegalName, "Legal name", 200);
        var currency = NormalizeCurrency(request.BaseCurrency);
        if (await companies.Query().AnyAsync(c => c.Code == code))
            throw new InvalidOperationException("A company with this code already exists in the tenant.");

        var company = await companies.AddAsync(new Company
        {
            Code = code,
            LegalName = legalName,
            TradingName = NormalizeOptional(request.TradingName, 200),
            RegistrationNumber = NormalizeOptional(request.RegistrationNumber, 100),
            TaxIdentifier = NormalizeOptional(request.TaxIdentifier, 100),
            BaseCurrency = currency,
            CountryCode = NormalizeOptional(request.CountryCode, 2)?.ToUpperInvariant()
        });
        await unitOfWork.SaveChangesAsync();
        return company;
    }

    public async Task UpdateCompanyAsync(int id, UpdateCompanyRequest request)
    {
        EnsureTenantResolved();
        var company = await GetTenantCompanyAsync(id)
            ?? throw new KeyNotFoundException("Company not found.");
        if (!request.IsActive && company.IsActive &&
            await branches.Query().AnyAsync(b => b.CompanyId == id && b.IsActive))
            throw new InvalidOperationException("Deactivate or reassign active branches before deactivating the company.");
        company.LegalName = NormalizeRequired(request.LegalName, "Legal name", 200);
        company.TradingName = NormalizeOptional(request.TradingName, 200);
        company.RegistrationNumber = NormalizeOptional(request.RegistrationNumber, 100);
        company.TaxIdentifier = NormalizeOptional(request.TaxIdentifier, 100);
        company.BaseCurrency = NormalizeCurrency(request.BaseCurrency);
        company.CountryCode = NormalizeOptional(request.CountryCode, 2)?.ToUpperInvariant();
        company.IsActive = request.IsActive;
        await companies.UpdateAsync(company);
        await unitOfWork.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<Branch>> GetBranchesAsync(int companyId, string? search = null)
    {
        EnsureTenantResolved();
        _ = await GetTenantCompanyAsync(companyId)
            ?? throw new KeyNotFoundException("Company not found.");
        IQueryable<Branch> query = branches.Query().Where(b => b.CompanyId == companyId).OrderBy(b => b.Code);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(b => b.Code.Contains(term) || b.Name.Contains(term)).OrderBy(b => b.Code);
        }
        return await query.ToListAsync();
    }

    public Task<Branch?> GetBranchAsync(int id)
    {
        EnsureTenantResolved();
        return GetTenantBranchAsync(id);
    }

    public async Task<Branch> CreateBranchAsync(CreateBranchRequest request)
    {
        EnsureTenantResolved();
        _ = await GetActiveCompanyAsync(request.CompanyId);
        var code = NormalizeRequired(request.Code, "Branch code", 32);
        if (await branches.Query().AnyAsync(b => b.CompanyId == request.CompanyId && b.Code == code))
            throw new InvalidOperationException("A branch with this code already exists in the company.");

        var branch = await branches.AddAsync(new Branch
        {
            CompanyId = request.CompanyId,
            Code = code,
            Name = NormalizeRequired(request.Name, "Branch name", 200),
            Address = NormalizeOptional(request.Address, 500),
            TimeZoneId = NormalizeRequired(request.TimeZoneId, "Time zone", 100)
        });
        await unitOfWork.SaveChangesAsync();
        return branch;
    }

    public async Task UpdateBranchAsync(int id, UpdateBranchRequest request)
    {
        EnsureTenantResolved();
        var branch = await GetTenantBranchAsync(id)
            ?? throw new KeyNotFoundException("Branch not found.");
        if (request.IsActive && !branch.IsActive)
            _ = await GetActiveCompanyAsync(branch.CompanyId);
        if (!request.IsActive && branch.IsActive &&
            await locations.Query().AnyAsync(location => location.BranchId == id))
            throw new InvalidOperationException("Reassign locations before deactivating the branch.");
        branch.Name = NormalizeRequired(request.Name, "Branch name", 200);
        branch.Address = NormalizeOptional(request.Address, 500);
        branch.TimeZoneId = NormalizeRequired(request.TimeZoneId, "Time zone", 100);
        branch.IsActive = request.IsActive;
        await branches.UpdateAsync(branch);
        await unitOfWork.SaveChangesAsync();
    }

    public async Task AssignLocationBranchAsync(int locationId, int branchId)
    {
        EnsureTenantResolved();
        var location = await locations.GetByIdAsync(locationId)
            ?? throw new KeyNotFoundException("Location not found.");
        if (location.IsDeleted ||
            !string.Equals(location.TenantId, tenantContext.TenantId, StringComparison.Ordinal))
            throw new KeyNotFoundException("Location not found.");
        var branch = await GetTenantBranchAsync(branchId)
            ?? throw new KeyNotFoundException("Branch not found.");
        if (!branch.IsActive)
            throw new InvalidOperationException("An inactive branch cannot own a location.");
        _ = await GetActiveCompanyAsync(branch.CompanyId);
        location.BranchId = branch.Id;
        await locations.UpdateAsync(location);
        await unitOfWork.SaveChangesAsync();
    }

    private async Task<Company?> GetTenantCompanyAsync(int id)
    {
        var company = await companies.GetByIdAsync(id);
        return company is not null &&
               string.Equals(company.TenantId, tenantContext.TenantId, StringComparison.Ordinal)
            ? company
            : null;
    }

    private async Task<Branch?> GetTenantBranchAsync(int id)
    {
        var branch = await branches.GetByIdAsync(id);
        return branch is not null &&
               string.Equals(branch.TenantId, tenantContext.TenantId, StringComparison.Ordinal)
            ? branch
            : null;
    }

    private async Task<Company> GetActiveCompanyAsync(int id)
    {
        var company = await GetTenantCompanyAsync(id);
        return company is { IsActive: true }
            ? company
            : throw new InvalidOperationException("The company does not exist in this tenant or is inactive.");
    }

    private void EnsureTenantResolved()
    {
        if (!tenantContext.IsResolved)
            throw new InvalidOperationException("A resolved tenant is required for organization operations.");
    }

    private static string NormalizeRequired(string? value, string name, int maxLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maxLength)
            throw new ArgumentException($"{name} is required and must be {maxLength} characters or fewer.");
        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null :
            normalized.Length <= maxLength ? normalized :
            throw new ArgumentException($"Value must be {maxLength} characters or fewer.");
    }

    private static string NormalizeCurrency(string? value)
    {
        var currency = NormalizeRequired(value, "Base currency", 3).ToUpperInvariant();
        if (currency.Length != 3 || currency.Any(c => c is < 'A' or > 'Z'))
            throw new ArgumentException("Base currency must be a three-letter ISO-style code.");
        return currency;
    }
}
