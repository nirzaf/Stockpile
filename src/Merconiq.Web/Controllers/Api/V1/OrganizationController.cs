using Asp.Versioning;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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

    [HttpPost("units/import")]
    [Authorize(Policy = CapabilityPolicies.TenantAdministrator)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportUnits([FromBody] ImportUnitsRequest request, CancellationToken cancellationToken)
    {
        var result = await imports.ImportUnitsAsync(request, cancellationToken);
        return result.Rejected > 0
            ? UnprocessableEntity(result)
            : Ok(ApiResponse<ImportUnitsResult>.CreateSuccess(result));
    }

    [HttpPost("items/import")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    // This bearer-token API is not cookie-authenticated, so browser CSRF tokens do not apply.
    // Keep the explicit validation marker for static security analysis while opting out at runtime.
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> ImportItems([FromBody] ImportItemsRequest request, CancellationToken cancellationToken)
    {
        var result = await imports.ImportItemsAsync(request, cancellationToken);
        return result.Rejected > 0
            ? UnprocessableEntity(result)
            : Ok(ApiResponse<ImportItemsResult>.CreateSuccess(result));
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
