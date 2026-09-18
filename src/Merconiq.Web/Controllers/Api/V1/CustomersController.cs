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

/// <summary>Company-scoped customer master API. It does not create sales or receivable documents.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/companies/{companyId:int}/customers")]
[Produces("application/json")]
[Authorize(Policy = "Api")]
[EnableRateLimiting("Api")]
public sealed class CustomersController(
    InventoryDbContext db,
    ICurrentUserAuthorization authorization) : ControllerBase
{
    private const int MaximumPageSize = 100;
    private const string CustomerCodeIndex = "UX_Customers_TenantId_CompanyId_CustomerCode";

    /// <summary>List customers belonging to the selected company.</summary>
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

        var query = db.Customers.Where(customer => customer.CompanyId == companyId);
        if (!includeInactive)
            query = query.Where(customer => customer.IsActive);

        var rows = await query
            .OrderBy(customer => customer.CustomerCode)
            .ThenBy(customer => customer.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > pageSize;
        var customers = rows.Take(pageSize).Select(ToResponse).ToArray();

        return Ok(ApiResponse<CustomerPageResponse>.CreateSuccess(
            new CustomerPageResponse(page, pageSize, hasMore, customers)));
    }

    /// <summary>Get one customer only when it belongs to the selected company.</summary>
    [HttpGet("{customerId:int}")]
    [Authorize(Policy = CapabilityPolicies.View)]
    public async Task<IActionResult> GetById(
        int companyId,
        int customerId,
        CancellationToken cancellationToken = default)
    {
        if (!await authorization.CanAccessCompanyAsync(User, companyId, CompanyCapability.View))
            return Forbid();

        var customer = await db.Customers.SingleOrDefaultAsync(
            candidate => candidate.Id == customerId && candidate.CompanyId == companyId,
            cancellationToken);
        return customer is null
            ? NotFound(ApiResponse<object>.CreateFailure("Customer not found."))
            : Ok(ApiResponse<CustomerResponse>.CreateSuccess(ToResponse(customer)));
    }

    /// <summary>Create a customer owned by the selected company.</summary>
    [HttpPost]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Create(
        int companyId,
        [FromBody] CustomerWriteRequest request,
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

        var code = NormalizeCode(request.CustomerCode);
        if (await CustomerCodeExistsAsync(companyId, code, cancellationToken))
            return CustomerCodeConflict();

        var customer = new Customer { CompanyId = companyId };
        ApplyRequest(customer, request, code);
        db.Customers.Add(customer);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsCustomerCodeConflict(exception))
        {
            return CustomerCodeConflict();
        }

        return CreatedAtAction(nameof(GetById), new { companyId, customerId = customer.Id },
            ApiResponse<CustomerResponse>.CreateSuccess(ToResponse(customer)));
    }

    /// <summary>Update customer master fields without changing its company ownership.</summary>
    [HttpPut("{customerId:int}")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Update(
        int companyId,
        int customerId,
        [FromBody] CustomerWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateRequest(request);
        if (validationError is not null)
            return BadRequest(ApiResponse<object>.CreateFailure(validationError));
        if (!await authorization.CanAccessCompanyAsync(User, companyId, CompanyCapability.Edit))
            return Forbid();

        var customer = await db.Customers.SingleOrDefaultAsync(
            candidate => candidate.Id == customerId && candidate.CompanyId == companyId,
            cancellationToken);
        if (customer is null)
            return NotFound(ApiResponse<object>.CreateFailure("Customer not found."));

        var code = NormalizeCode(request.CustomerCode);
        if (customer.CustomerCode != code &&
            await CustomerCodeExistsAsync(companyId, code, cancellationToken))
        {
            return CustomerCodeConflict();
        }

        ApplyRequest(customer, request, code);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsCustomerCodeConflict(exception))
        {
            return CustomerCodeConflict();
        }

        return NoContent();
    }

    /// <summary>Deactivate a customer without deleting its audited master record.</summary>
    [HttpPost("{customerId:int}/deactivate")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public Task<IActionResult> Deactivate(
        int companyId,
        int customerId,
        CancellationToken cancellationToken = default) =>
        SetActiveAsync(companyId, customerId, false, cancellationToken);

    /// <summary>Reactivate a previously deactivated customer.</summary>
    [HttpPost("{customerId:int}/reactivate")]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    [ValidateAntiForgeryToken]
    [IgnoreAntiforgeryToken]
    public Task<IActionResult> Reactivate(
        int companyId,
        int customerId,
        CancellationToken cancellationToken = default) =>
        SetActiveAsync(companyId, customerId, true, cancellationToken);

    private async Task<IActionResult> SetActiveAsync(
        int companyId,
        int customerId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        if (!await authorization.CanAccessCompanyAsync(User, companyId, CompanyCapability.Edit))
            return Forbid();

        var customer = await db.Customers.SingleOrDefaultAsync(
            candidate => candidate.Id == customerId && candidate.CompanyId == companyId,
            cancellationToken);
        if (customer is null)
            return NotFound(ApiResponse<object>.CreateFailure("Customer not found."));

        if (customer.IsActive != isActive)
        {
            customer.IsActive = isActive;
            await db.SaveChangesAsync(cancellationToken);
        }

        return NoContent();
    }

    private Task<bool> CustomerCodeExistsAsync(
        int companyId,
        string code,
        CancellationToken cancellationToken) =>
        db.Customers.AnyAsync(
            customer => customer.CompanyId == companyId && customer.CustomerCode == code,
            cancellationToken);

    private static void ApplyRequest(Customer customer, CustomerWriteRequest request, string code)
    {
        customer.CustomerCode = code;
        customer.Name = request.Name.Trim();
        customer.ContactEmail = NormalizeOptional(request.ContactEmail);
        customer.ContactPhone = NormalizeOptional(request.ContactPhone);
        customer.BillingAddress = NormalizeOptional(request.BillingAddress);
        customer.ShippingAddress = NormalizeOptional(request.ShippingAddress);
        customer.PaymentTermDays = request.PaymentTermDays;
    }

    private static string NormalizeCode(string value) => value.Trim().ToUpperInvariant();

    private static string? ValidateRequest(CustomerWriteRequest request)
    {
        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(request);
        if (!Validator.TryValidateObject(request, validationContext, validationResults, validateAllProperties: true))
            return validationResults[0].ErrorMessage ?? "Customer fields are invalid.";
        if (string.IsNullOrWhiteSpace(request.CustomerCode))
            return "CustomerCode must not be blank.";
        if (string.IsNullOrWhiteSpace(request.Name))
            return "Name must not be blank.";
        return null;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static bool IsCustomerCodeConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: CustomerCodeIndex
        };

    private static ConflictObjectResult CustomerCodeConflict() =>
        new(ApiResponse<object>.CreateFailure(
            "A customer with this code already exists in this company."));

    private static CustomerResponse ToResponse(Customer customer) => new(
        customer.Id,
        customer.CompanyId,
        customer.CustomerCode,
        customer.Name,
        customer.ContactEmail,
        customer.ContactPhone,
        customer.BillingAddress,
        customer.ShippingAddress,
        customer.PaymentTermDays,
        customer.IsActive);
}

/// <summary>Company-local customer master fields. Tenant and company ownership come from the route and session.</summary>
public sealed class CustomerWriteRequest
{
    [Required, StringLength(64)]
    public string CustomerCode { get; init; } = string.Empty;

    [Required, StringLength(200)]
    public string Name { get; init; } = string.Empty;

    [EmailAddress, StringLength(254)]
    public string? ContactEmail { get; init; }

    [StringLength(40)]
    public string? ContactPhone { get; init; }

    [StringLength(500)]
    public string? BillingAddress { get; init; }

    [StringLength(500)]
    public string? ShippingAddress { get; init; }

    [Range(0, int.MaxValue)]
    public int? PaymentTermDays { get; init; }
}

/// <summary>A bounded page of customers for one company.</summary>
public sealed record CustomerPageResponse(
    int Page,
    int PageSize,
    bool HasMore,
    IReadOnlyList<CustomerResponse> Items);

/// <summary>Customer details exposed by the company-scoped API.</summary>
public sealed record CustomerResponse(
    int Id,
    int CompanyId,
    string CustomerCode,
    string Name,
    string? ContactEmail,
    string? ContactPhone,
    string? BillingAddress,
    string? ShippingAddress,
    int? PaymentTermDays,
    bool IsActive);
