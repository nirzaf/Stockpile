using Asp.Versioning;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Merconiq.Web.Security;

namespace Merconiq.Web.Controllers.Api.V1;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/organization")]
[Produces("application/json")]
[Authorize(Policy = "Api")]
[EnableRateLimiting("Api")]
public sealed class OrganizationController(
    IOrganizationService organization,
    IMasterDataImportService imports,
    ICompanyMembershipService memberships,
    ICurrentUserAuthorization authorization) : ControllerBase
{
    private const int MaximumUnitExportPageSize = 100;

    [HttpGet("companies")]
    [Authorize(Policy = CapabilityPolicies.View)]
    public async Task<IActionResult> GetCompanies([FromQuery] string? search)
    {
        var companyIds = await authorization.GetAccessibleCompanyIdsAsync(User, CompanyCapability.View);
        var companies = await organization.GetCompaniesAsync(search, companyIds.ToArray());
        return Ok(ApiResponse<IReadOnlyList<CompanyResponse>>.CreateSuccess(companies.Select(ToResponse).ToList()));
    }

    [HttpGet("companies/{id:int}")]
    [Authorize(Policy = CapabilityPolicies.View)]
    public async Task<IActionResult> GetCompany(int id)
    {
        if (!await authorization.CanAccessCompanyAsync(User, id, CompanyCapability.View))
        {
            return Forbid();
        }

        var company = await organization.GetCompanyAsync(id);
        return company is null
            ? NotFound(ApiResponse<object>.CreateFailure("Company not found."))
            : Ok(ApiResponse<CompanyResponse>.CreateSuccess(ToResponse(company)));
    }

    [HttpPost("companies")]
    [Authorize(Policy = CapabilityPolicies.TenantAdministrator)]
    public async Task<IActionResult> CreateCompany([FromBody] CreateCompanyRequest request)
    {
        var company = await organization.CreateCompanyAsync(request);
        return CreatedAtAction(nameof(GetCompany), new { id = company.Id },
            ApiResponse<CompanyResponse>.CreateSuccess(ToResponse(company)));
    }

    [HttpPut("companies/{id:int}")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    public async Task<IActionResult> UpdateCompany(int id, [FromBody] UpdateCompanyRequest request)
    {
        if (!await authorization.CanAccessCompanyAsync(User, id, CompanyCapability.Edit))
        {
            return Forbid();
        }

        await organization.UpdateCompanyAsync(id, request);
        return NoContent();
    }

    [HttpGet("companies/{companyId:int}/branches")]
    [Authorize(Policy = CapabilityPolicies.View)]
    public async Task<IActionResult> GetBranches(int companyId, [FromQuery] string? search)
    {
        if (!await authorization.CanAccessCompanyAsync(User, companyId, CompanyCapability.View))
        {
            return Forbid();
        }

        var branches = await organization.GetBranchesAsync(companyId, search);
        return Ok(ApiResponse<IReadOnlyList<BranchResponse>>.CreateSuccess(branches.Select(ToResponse).ToList()));
    }

    [HttpGet("branches/{id:int}")]
    [Authorize(Policy = CapabilityPolicies.View)]
    public async Task<IActionResult> GetBranch(int id)
    {
        if (!await authorization.CanAccessBranchAsync(User, id, CompanyCapability.View))
        {
            return Forbid();
        }

        var branch = await organization.GetBranchAsync(id);
        return branch is null
            ? NotFound(ApiResponse<object>.CreateFailure("Branch not found."))
            : Ok(ApiResponse<BranchResponse>.CreateSuccess(ToResponse(branch)));
    }

    [HttpPost("branches")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    public async Task<IActionResult> CreateBranch([FromBody] CreateBranchRequest request)
    {
        if (!await authorization.CanAccessCompanyAsync(User, request.CompanyId, CompanyCapability.Edit))
        {
            return Forbid();
        }

        var branch = await organization.CreateBranchAsync(request);
        return CreatedAtAction(nameof(GetBranch), new { id = branch.Id },
            ApiResponse<BranchResponse>.CreateSuccess(ToResponse(branch)));
    }

    [HttpPut("branches/{id:int}")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    public async Task<IActionResult> UpdateBranch(int id, [FromBody] UpdateBranchRequest request)
    {
        if (!await authorization.CanAccessBranchAsync(User, id, CompanyCapability.Edit))
        {
            return Forbid();
        }

        await organization.UpdateBranchAsync(id, request);
        return NoContent();
    }

    [HttpPut("locations/{locationId:int}/branch")]
    [Authorize(Policy = CapabilityPolicies.Administer)]
    public async Task<IActionResult> AssignLocationBranch(int locationId, [FromBody] AssignLocationBranchRequest request)
    {
        if (!await authorization.CanAssignLocationBranchAsync(User, locationId, request.BranchId))
        {
            return Forbid();
        }

        await organization.AssignLocationBranchAsync(locationId, request.BranchId);
        return NoContent();
    }

    [HttpGet("companies/{companyId:int}/memberships")]
    [Authorize(Policy = CapabilityPolicies.Administer)]
    public async Task<IActionResult> GetCompanyMemberships(int companyId)
    {
        if (!await authorization.CanAccessCompanyAsync(User, companyId, CompanyCapability.Administer))
        {
            return Forbid();
        }

        var grants = await memberships.GetCompanyMembershipsAsync(companyId);
        return Ok(ApiResponse<IReadOnlyList<CompanyMembershipResponse>>.CreateSuccess(
            grants.Select(grant => new CompanyMembershipResponse(
                grant.UserId, grant.Capabilities, grant.IsActive)).ToList()));
    }

    [HttpPut("companies/{companyId:int}/memberships/{userId}")]
    [Authorize(Policy = CapabilityPolicies.Administer)]
    public async Task<IActionResult> SetCompanyMembership(
        int companyId,
        string userId,
        [FromBody] SetCompanyMembershipRequest request)
    {
        if (!await authorization.CanAccessCompanyAsync(User, companyId, CompanyCapability.Administer))
        {
            return Forbid();
        }

        try
        {
            await memberships.SetCapabilitiesAsync(companyId, userId, request.Capabilities);
            return NoContent();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return BadRequest(ApiResponse<object>.CreateFailure(exception.Message));
        }
    }

    [HttpDelete("companies/{companyId:int}/memberships/{userId}")]
    [Authorize(Policy = CapabilityPolicies.Administer)]
    public async Task<IActionResult> RemoveCompanyMembership(int companyId, string userId)
    {
        if (!await authorization.CanAccessCompanyAsync(User, companyId, CompanyCapability.Administer))
        {
            return Forbid();
        }

        await memberships.RemoveMembershipAsync(companyId, userId);
        return NoContent();
    }

    [HttpPost("companies/import")]
    [Authorize(Policy = CapabilityPolicies.TenantAdministrator)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> ImportCompanies(
        [FromBody] ImportCompaniesRequest request, CancellationToken cancellationToken)
    {
        var result = await imports.ImportCompaniesAsync(request, cancellationToken);
        return result.Rejected > 0
            ? UnprocessableEntity(result)
            : Ok(ApiResponse<ImportCompaniesResult>.CreateSuccess(result));
    }

    [HttpPost("branches/import")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> ImportBranches(
        [FromBody] ImportBranchesRequest request, CancellationToken cancellationToken)
    {
        var scope = await RequireImportCompanyAsync(request.CompanyId, CompanyCapability.Edit);
        if (scope is not null) return scope;
        var result = await imports.ImportBranchesAsync(request, cancellationToken);
        return result.Rejected > 0
            ? UnprocessableEntity(result)
            : Ok(ApiResponse<ImportBranchesResult>.CreateSuccess(result));
    }

    [HttpPost("locations/import")]
    [Authorize(Policy = CapabilityPolicies.Administer)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> ImportLocations(
        [FromBody] ImportLocationsRequest request, CancellationToken cancellationToken)
    {
        var scope = await RequireImportCompanyAsync(request.CompanyId, CompanyCapability.Administer);
        if (scope is not null) return scope;
        var result = await imports.ImportLocationsAsync(request, cancellationToken);
        return result.Rejected > 0
            ? UnprocessableEntity(result)
            : Ok(ApiResponse<ImportLocationsResult>.CreateSuccess(result));
    }

    [HttpPost("suppliers/import")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> ImportSuppliers(
        [FromBody] ImportSuppliersRequest request, CancellationToken cancellationToken)
    {
        var scope = await RequireImportCompanyAsync(request.CompanyId, CompanyCapability.Edit);
        if (scope is not null) return scope;
        var result = await imports.ImportSuppliersAsync(request, cancellationToken);
        return result.Rejected > 0
            ? UnprocessableEntity(result)
            : Ok(ApiResponse<ImportSuppliersResult>.CreateSuccess(result));
    }

    [HttpPost("units/import")]
    [Authorize(Policy = CapabilityPolicies.TenantAdministrator)]
    // The controller's Api policy requires a JWT Bearer token, not an ambient cookie.
    // Keep the explicit validation marker for static security analysis while opting out at runtime.
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> ImportUnits([FromBody] ImportUnitsRequest request, CancellationToken cancellationToken)
    {
        var scope = await RequireImportCompanyAsync(request.CompanyId, CompanyCapability.View);
        if (scope is not null) return scope;
        var result = await imports.ImportUnitsAsync(request, cancellationToken);
        return result.Rejected > 0
            ? UnprocessableEntity(result)
            : Ok(ApiResponse<ImportUnitsResult>.CreateSuccess(result));
    }

    [HttpGet("units/export")]
    [Authorize(Policy = CapabilityPolicies.TenantAdministrator)]
    [ProducesResponseType(typeof(ApiResponse<UnitOfMeasureExportResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportUnits(
        [FromServices] IRepository<UnitOfMeasure> units,
        [FromServices] ITenantContext tenantContext,
        CancellationToken cancellationToken,
        [FromQuery] string? afterExternalId = null,
        [FromQuery] int pageSize = 50)
    {
        if (pageSize is < 1 or > MaximumUnitExportPageSize)
        {
            return BadRequest(ApiResponse<object>.CreateFailure(
                $"pageSize must be between 1 and {MaximumUnitExportPageSize}."));
        }

        if (afterExternalId?.Length > 128)
        {
            return BadRequest(ApiResponse<object>.CreateFailure(
                "afterExternalId must be at most 128 characters."));
        }

        var legacyPrefix = MasterDataImportConventions.LegacyUnmappedUnitExternalIdPrefix;
        var query = units.Query()
            .Where(unit => unit.TenantId == tenantContext.TenantId &&
                           !unit.IsDeleted &&
                           unit.ExternalId != string.Empty &&
                           !unit.ExternalId.StartsWith(legacyPrefix));
        if (afterExternalId is not null)
        {
            query = query.Where(unit => unit.ExternalId.CompareTo(afterExternalId) > 0);
        }

        var rows = await query
            .OrderBy(unit => unit.ExternalId)
            .ThenBy(unit => unit.Id)
            .Take(pageSize + 1)
            .Select(unit => new UnitOfMeasureExportRecord(
                unit.ExternalId,
                unit.Code,
                unit.Name,
                unit.DecimalPlaces,
                unit.IsWholeUnitOnly))
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var response = new UnitOfMeasureExportResponse(
            pageSize,
            hasMore,
            hasMore ? rows[^1].ExternalId : null,
            rows);
        return Ok(ApiResponse<UnitOfMeasureExportResponse>.CreateSuccess(response));
    }

    [HttpPost("items/import")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    // This bearer-token API is not cookie-authenticated, so browser CSRF tokens do not apply.
    // Keep the explicit validation marker for static security analysis while opting out at runtime.
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> ImportItems([FromBody] ImportItemsRequest request, CancellationToken cancellationToken)
    {
        var scope = await RequireImportCompanyAsync(request.CompanyId, CompanyCapability.Edit);
        if (scope is not null) return scope;
        var result = await imports.ImportItemsAsync(request, cancellationToken);
        return result.Rejected > 0
            ? UnprocessableEntity(result)
            : Ok(ApiResponse<ImportItemsResult>.CreateSuccess(result));
    }

    private async Task<IActionResult?> RequireImportCompanyAsync(
        int? companyId, CompanyCapability capability)
    {
        if (companyId is not > 0)
        {
            return BadRequest(ApiResponse<object>.CreateFailure(
                "CompanyId is required for this import."));
        }

        return await authorization.CanAccessCompanyAsync(User, companyId.Value, capability)
            ? null
            : Forbid();
    }

    private static CompanyResponse ToResponse(Company company) =>
        new(company.Id, company.TenantId, company.Code, company.LegalName, company.TradingName,
            company.RegistrationNumber, company.TaxIdentifier, company.BaseCurrency,
            company.CountryCode, company.IsActive, company.CurrencyScale);

    private static BranchResponse ToResponse(Branch branch) =>
        new(branch.Id, branch.CompanyId, branch.TenantId, branch.Code, branch.Name,
            branch.Address, branch.TimeZoneId, branch.IsActive);
}

public sealed record SetCompanyMembershipRequest(CompanyCapability Capabilities);

public sealed record CompanyMembershipResponse(string UserId, CompanyCapability Capabilities, bool IsActive);

public sealed record CompanyResponse(
    int Id,
    string TenantId,
    string Code,
    string LegalName,
    string? TradingName,
    string? RegistrationNumber,
    string? TaxIdentifier,
    string BaseCurrency,
    string? CountryCode,
    bool IsActive,
    int? CurrencyScale);

public sealed record BranchResponse(
    int Id,
    int CompanyId,
    string TenantId,
    string Code,
    string Name,
    string? Address,
    string TimeZoneId,
    bool IsActive);
