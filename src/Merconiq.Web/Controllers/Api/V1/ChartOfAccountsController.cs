using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Merconiq.Core.Entities;
using Merconiq.Core.Models;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Merconiq.Web.Controllers.Api.V1;

/// <summary>Company-scoped chart-of-account configuration; this API does not post journals.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/companies/{companyId:int}/chart-of-accounts")]
[Produces("application/json")]
[Authorize(Policy = "Api")]
[EnableRateLimiting("Api")]
public sealed class ChartOfAccountsController(
    InventoryDbContext db,
    ICurrentUserAuthorization authorization) : ControllerBase
{
    private const int MaximumPageSize = 100;
    private const string AccountCodeIndex = "UX_ChartAccounts_Tenant_Company_Code";

    /// <summary>List account definitions belonging to one authorized company.</summary>
    [HttpGet]
    [Authorize(Policy = CapabilityPolicies.View)]
    public async Task<IActionResult> GetAll(
        int companyId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        if (!await authorization.CanAccessCompanyAsync(User, companyId, CompanyCapability.View))
            return Forbid();
        if (page < 1 || pageSize is < 1 or > MaximumPageSize)
        {
            return BadRequest(ApiResponse<object>.CreateFailure(
                $"Page must be at least 1 and pageSize must be between 1 and {MaximumPageSize}."));
        }

        var offset = (long)(page - 1) * pageSize;
        if (offset > int.MaxValue)
            return BadRequest(ApiResponse<object>.CreateFailure("The requested page is too large."));

        var query = db.ChartOfAccounts.Where(account => account.CompanyId == companyId);
        if (!includeInactive)
            query = query.Where(account => account.IsActive);

        var rows = await query
            .OrderBy(account => account.AccountCode)
            .ThenBy(account => account.Id)
            .Skip((int)offset)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > pageSize;
        var accounts = rows.Take(pageSize).Select(ToResponse).ToArray();

        return Ok(ApiResponse<ChartOfAccountPageResponse>.CreateSuccess(
            new ChartOfAccountPageResponse(page, pageSize, hasMore, accounts)));
    }

    /// <summary>Create an account definition using company-supplied, non-standardized values.</summary>
    [HttpPost]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Create(
        int companyId,
        [FromBody] ChartOfAccountWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateRequest(request);
        if (validationError is not null)
            return BadRequest(ApiResponse<object>.CreateFailure(validationError));
        if (!await authorization.CanAccessCompanyAsync(User, companyId, CompanyCapability.Edit))
            return Forbid();

        if (!await db.Companies.AnyAsync(
                company => company.Id == companyId && company.IsActive,
                cancellationToken))
        {
            return NotFound(ApiResponse<object>.CreateFailure("Active company not found."));
        }

        var accountCode = request.AccountCode.Trim();
        if (await AccountCodeExistsAsync(companyId, accountCode, cancellationToken))
            return AccountCodeConflict();

        if (request.ParentAccountId is int parentAccountId)
        {
            var parent = await db.ChartOfAccounts.SingleOrDefaultAsync(
                account => account.Id == parentAccountId && account.CompanyId == companyId,
                cancellationToken);
            if (parent is null || !parent.IsActive || !parent.IsGroupAccount)
            {
                return BadRequest(ApiResponse<object>.CreateFailure(
                    "ParentAccountId must identify an active group account in this company."));
            }
        }

        var account = new ChartOfAccount
        {
            CompanyId = companyId,
            AccountCode = accountCode,
            Name = request.Name.Trim(),
            AccountType = request.AccountType.Trim(),
            ParentAccountId = request.ParentAccountId,
            IsGroupAccount = request.IsGroupAccount,
            IsActive = request.IsActive
        };
        db.ChartOfAccounts.Add(account);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsAccountCodeConflict(exception))
        {
            return AccountCodeConflict();
        }

        return CreatedAtAction(
            nameof(GetAll),
            new { companyId },
            ApiResponse<ChartOfAccountResponse>.CreateSuccess(ToResponse(account)));
    }

    private Task<bool> AccountCodeExistsAsync(
        int companyId,
        string accountCode,
        CancellationToken cancellationToken) =>
        db.ChartOfAccounts.AnyAsync(
            account => account.CompanyId == companyId && account.AccountCode == accountCode,
            cancellationToken);

    private static string? ValidateRequest(ChartOfAccountWriteRequest request)
    {
        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(request);
        if (!Validator.TryValidateObject(request, validationContext, validationResults, validateAllProperties: true))
            return validationResults[0].ErrorMessage ?? "Chart-of-account fields are invalid.";
        if (string.IsNullOrWhiteSpace(request.AccountCode) ||
            string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.AccountType))
        {
            return "AccountCode, Name, and AccountType must not be blank.";
        }

        return null;
    }

    private static bool IsAccountCodeConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: AccountCodeIndex
        };

    private static ConflictObjectResult AccountCodeConflict() =>
        new(ApiResponse<object>.CreateFailure(
            "An account with this code already exists in this company."));

    private static ChartOfAccountResponse ToResponse(ChartOfAccount account) => new(
        account.Id,
        account.CompanyId,
        account.AccountCode,
        account.Name,
        account.AccountType,
        account.ParentAccountId,
        account.IsGroupAccount,
        account.IsActive);
}

/// <summary>Company-supplied account-definition fields; ownership is taken from the route and session.</summary>
public sealed class ChartOfAccountWriteRequest
{
    [Required, StringLength(64)]
    public string AccountCode { get; init; } = string.Empty;

    [Required, StringLength(200)]
    public string Name { get; init; } = string.Empty;

    [Required, StringLength(64)]
    public string AccountType { get; init; } = string.Empty;

    public int? ParentAccountId { get; init; }

    public bool IsGroupAccount { get; init; }

    public bool IsActive { get; init; } = true;
}

/// <summary>A bounded page of chart-of-account definitions for one company.</summary>
public sealed record ChartOfAccountPageResponse(
    int Page,
    int PageSize,
    bool HasMore,
    IReadOnlyList<ChartOfAccountResponse> Items);

/// <summary>Configuration fields exposed by the company-scoped chart-of-accounts API.</summary>
public sealed record ChartOfAccountResponse(
    int Id,
    int CompanyId,
    string AccountCode,
    string Name,
    string AccountType,
    int? ParentAccountId,
    bool IsGroupAccount,
    bool IsActive);
