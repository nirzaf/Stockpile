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
public sealed class OrganizationController(IOrganizationService organization, IMasterDataImportService imports) : ControllerBase
{
    [HttpGet("companies")]
    public async Task<IActionResult> GetCompanies([FromQuery] string? search) =>
        Ok(ApiResponse<IReadOnlyList<CompanyResponse>>.CreateSuccess(
            (await organization.GetCompaniesAsync(search)).Select(ToResponse).ToList()));

    [HttpGet("companies/{id:int}")]
    public async Task<IActionResult> GetCompany(int id)
    {
        var company = await organization.GetCompanyAsync(id);
        return company is null
            ? NotFound(ApiResponse<object>.CreateFailure("Company not found."))
            : Ok(ApiResponse<CompanyResponse>.CreateSuccess(ToResponse(company)));
    }

    [HttpPost("companies")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
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
        await organization.UpdateCompanyAsync(id, request);
        return NoContent();
    }

    [HttpGet("companies/{companyId:int}/branches")]
    public async Task<IActionResult> GetBranches(int companyId, [FromQuery] string? search) =>
        Ok(ApiResponse<IReadOnlyList<BranchResponse>>.CreateSuccess(
            (await organization.GetBranchesAsync(companyId, search)).Select(ToResponse).ToList()));

    [HttpGet("branches/{id:int}")]
    public async Task<IActionResult> GetBranch(int id)
    {
        var branch = await organization.GetBranchAsync(id);
        return branch is null
            ? NotFound(ApiResponse<object>.CreateFailure("Branch not found."))
            : Ok(ApiResponse<BranchResponse>.CreateSuccess(ToResponse(branch)));
    }

    [HttpPost("branches")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    public async Task<IActionResult> CreateBranch([FromBody] CreateBranchRequest request)
    {
        var branch = await organization.CreateBranchAsync(request);
        return CreatedAtAction(nameof(GetBranch), new { id = branch.Id },
            ApiResponse<BranchResponse>.CreateSuccess(ToResponse(branch)));
    }

    [HttpPut("branches/{id:int}")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    public async Task<IActionResult> UpdateBranch(int id, [FromBody] UpdateBranchRequest request)
    {
        await organization.UpdateBranchAsync(id, request);
        return NoContent();
    }

    [HttpPut("locations/{locationId:int}/branch")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    public async Task<IActionResult> AssignLocationBranch(int locationId, [FromBody] AssignLocationBranchRequest request)
    {
        await organization.AssignLocationBranchAsync(locationId, request.BranchId);
        return NoContent();
    }

    [HttpPost("units/import")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportUnits([FromBody] ImportUnitsRequest request, CancellationToken cancellationToken)
    {
        var result = await imports.ImportUnitsAsync(request, cancellationToken);
        return result.Rejected > 0
            ? UnprocessableEntity(result)
            : Ok(ApiResponse<ImportUnitsResult>.CreateSuccess(result));
    }

    private static CompanyResponse ToResponse(Company company) =>
        new(company.Id, company.TenantId, company.Code, company.LegalName, company.TradingName,
            company.RegistrationNumber, company.TaxIdentifier, company.BaseCurrency,
            company.CountryCode, company.IsActive);

    private static BranchResponse ToResponse(Branch branch) =>
        new(branch.Id, branch.CompanyId, branch.TenantId, branch.Code, branch.Name,
            branch.Address, branch.TimeZoneId, branch.IsActive);
}

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
    bool IsActive);

public sealed record BranchResponse(
    int Id,
    int CompanyId,
    string TenantId,
    string Code,
    string Name,
    string? Address,
    string TimeZoneId,
    bool IsActive);
