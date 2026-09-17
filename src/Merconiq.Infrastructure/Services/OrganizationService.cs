using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Merconiq.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Infrastructure.Services;

/// <summary>Enforces tenant and company boundaries before organization writes.</summary>
public sealed class OrganizationService(
    IRepository<Company> companies,
    IRepository<Branch> branches,
    IRepository<Location> locations,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext,
    InventoryDbContext context) : IOrganizationService
{
    public async Task<IReadOnlyList<Company>> GetCompaniesAsync(
        string? search = null,
        IReadOnlyCollection<int>? accessibleCompanyIds = null)
    {
        EnsureTenantResolved();
        IQueryable<Company> query = companies.Query().OrderBy(c => c.Code);
        if (accessibleCompanyIds is not null)
        {
            if (accessibleCompanyIds.Count == 0)
            {
                return [];
            }

            query = query.Where(company => accessibleCompanyIds.Contains(company.Id));
        }

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
        Company? company = null;
        await ExecuteOrganizationWriteAsync(async () =>
        {
            var code = NormalizeRequired(request.Code, "Company code", 32);
            var legalName = NormalizeRequired(request.LegalName, "Legal name", 200);
            var currency = NormalizeCurrency(request.BaseCurrency);
            var currencyScale = NormalizeCurrencyScale(request.CurrencyScale);
            if (await companies.Query().AnyAsync(c => c.Code == code))
                throw new InvalidOperationException("A company with this code already exists in the tenant.");

            company = await companies.AddAsync(new Company
            {
                Code = code,
                LegalName = legalName,
                TradingName = NormalizeOptional(request.TradingName, 200),
                RegistrationNumber = NormalizeOptional(request.RegistrationNumber, 100),
                TaxIdentifier = NormalizeOptional(request.TaxIdentifier, 100),
                BaseCurrency = currency,
                CurrencyScale = currencyScale,
                CountryCode = NormalizeOptional(request.CountryCode, 2)?.ToUpperInvariant()
            });
        });
        return company!;
    }

    public async Task UpdateCompanyAsync(int id, UpdateCompanyRequest request)
    {
        EnsureTenantResolved();
        await ExecuteOrganizationWriteAsync(async () =>
        {
            var company = await GetTenantCompanyAsync(id)
                ?? throw new KeyNotFoundException("Company not found.");
            if (!request.IsActive && company.IsActive &&
                await branches.Query().AnyAsync(b => b.CompanyId == id && b.IsActive))
                throw new InvalidOperationException("Deactivate or reassign active branches before deactivating the company.");
            var baseCurrency = NormalizeCurrency(request.BaseCurrency);
            var currencyScale = request.CurrencyScale.HasValue
                ? NormalizeCurrencyScale(request.CurrencyScale)
                : company.CurrencyScale;
            var currencyChanged = !string.Equals(company.BaseCurrency, baseCurrency, StringComparison.Ordinal);
            var currencyScaleChanged = company.CurrencyScale.HasValue && company.CurrencyScale != currencyScale;
            if ((currencyChanged || currencyScaleChanged) && await HasPostedStockActivityAsync(id))
            {
                if (currencyChanged)
                    throw new InvalidOperationException("A company's base currency cannot change after posted stock activity.");
                throw new InvalidOperationException("A company's currency scale cannot change after posted stock activity.");
            }
            company.LegalName = NormalizeRequired(request.LegalName, "Legal name", 200);
            company.TradingName = NormalizeOptional(request.TradingName, 200);
            company.RegistrationNumber = NormalizeOptional(request.RegistrationNumber, 100);
            company.TaxIdentifier = NormalizeOptional(request.TaxIdentifier, 100);
            company.BaseCurrency = baseCurrency;
            company.CurrencyScale = currencyScale;
            company.CountryCode = NormalizeOptional(request.CountryCode, 2)?.ToUpperInvariant();
            company.IsActive = request.IsActive;
            await companies.UpdateAsync(company);
        });
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
        Branch? branch = null;
        await ExecuteOrganizationWriteAsync(async () =>
        {
            _ = await GetActiveCompanyAsync(request.CompanyId);
            var code = NormalizeRequired(request.Code, "Branch code", 32);
            if (await branches.Query().AnyAsync(b => b.CompanyId == request.CompanyId && b.Code == code))
                throw new InvalidOperationException("A branch with this code already exists in the company.");

            branch = await branches.AddAsync(new Branch
            {
                CompanyId = request.CompanyId,
                Code = code,
                Name = NormalizeRequired(request.Name, "Branch name", 200),
                Address = NormalizeOptional(request.Address, 500),
                TimeZoneId = NormalizeRequired(request.TimeZoneId, "Time zone", 100)
            });
        });
        return branch!;
    }

    public async Task UpdateBranchAsync(int id, UpdateBranchRequest request)
    {
        EnsureTenantResolved();
        var companyId = await branches.Query()
            .Where(branch => branch.Id == id)
            .Select(branch => (int?)branch.CompanyId)
            .SingleOrDefaultAsync()
            ?? throw new KeyNotFoundException("Branch not found.");

        await ExecuteOrganizationWriteAsync(async () =>
        {
            var branch = await GetTenantBranchAsync(id)
                ?? throw new KeyNotFoundException("Branch not found.");
            if (branch.CompanyId != companyId)
                throw new InvalidOperationException("A branch's company ownership cannot be changed.");
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
        });
    }

    public async Task AssignLocationBranchAsync(int locationId, int branchId)
    {
        EnsureTenantResolved();
        await ExecuteOrganizationWriteAsync(async () =>
        {
            await unitOfWork.AcquireLocationLocksAsync([locationId]);
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
            if (location.BranchId.HasValue && location.BranchId != branch.Id &&
                await context.StockTransactions.AnyAsync(transaction =>
                    transaction.FromLocationId == locationId || transaction.ToLocationId == locationId))
                throw new InvalidOperationException(
                    "A location's branch ownership cannot change after posted stock activity.");
            location.BranchId = branch.Id;
            await locations.UpdateAsync(location);
        });
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

    private async Task<bool> HasPostedStockActivityAsync(int companyId)
    {
        var branchIds = context.Branches
            .Where(branch => branch.CompanyId == companyId)
            .Select(branch => branch.Id);
        var locationIds = context.Locations
            .Where(location => location.BranchId.HasValue && branchIds.Contains(location.BranchId.Value))
            .Select(location => location.Id);

        return await context.StockTransactions.AnyAsync(transaction =>
            locationIds.Contains(transaction.FromLocationId) ||
            (transaction.ToLocationId.HasValue && locationIds.Contains(transaction.ToLocationId.Value)));
    }

    private Task ExecuteOrganizationWriteAsync(Func<Task> operation) =>
        unitOfWork.ExecuteMasterDataWriteAsync(async () =>
        {
            if (context.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                // One tenant-scoped transaction lock serializes company, branch and location
                // ownership transitions, including checks that span several rows.
                await unitOfWork.AcquireTenantOperationLockAsync("organization-state");
            }

            await operation();
        });

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

    private static int NormalizeCurrencyScale(int? value)
    {
        if (!value.HasValue || value.Value is < 0 or > 4)
            throw new ArgumentException("Currency scale must be supplied and must be between 0 and 4.");
        return value.Value;
    }
}
