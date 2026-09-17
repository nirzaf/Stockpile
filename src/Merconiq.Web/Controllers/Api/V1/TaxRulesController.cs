using Asp.Versioning;
using Merconiq.Core.Entities;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Models;
using Merconiq.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Merconiq.Web.Security;

namespace Merconiq.Web.Controllers.Api.V1;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/tax-rules")]
[Produces("application/json")]
[Authorize(Policy = "Api")]
[EnableRateLimiting("Api")]
public sealed class TaxRulesController(
    IRepository<TaxRule> repository,
    IUnitOfWork unitOfWork) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = CapabilityPolicies.View)]
    public async Task<IActionResult> GetAll()
    {
        var rules = (await repository.GetAllAsync())
            .OrderBy(rule => rule.Code)
            .ThenByDescending(rule => rule.EffectiveFromUtc)
            .Select(ToResponse)
            .ToList();
        return Ok(ApiResponse<IReadOnlyList<TaxRuleResponse>>.CreateSuccess(rules));
    }

    [HttpGet("{id:int}")]
    [Authorize(Policy = CapabilityPolicies.View)]
    public async Task<IActionResult> GetById(int id)
    {
        var rule = await repository.GetByIdAsync(id);
        return rule is null
            ? NotFound(ApiResponse<object>.CreateFailure("Tax rule not found."))
            : Ok(ApiResponse<TaxRuleResponse>.CreateSuccess(ToResponse(rule)));
    }

    [HttpPost]
    [Authorize(Policy = CapabilityPolicies.Edit)]
    public async Task<IActionResult> Create([FromBody] TaxRuleRequest request)
    {
        try
        {
            var code = Validate(request);
            var effectiveFrom = NormalizeUtc(request.EffectiveFromUtc);
            DateTime? effectiveTo = request.EffectiveToUtc.HasValue
                ? NormalizeUtc(request.EffectiveToUtc.Value)
                : null;
            if (effectiveTo.HasValue && effectiveTo <= effectiveFrom)
            {
                throw new ArgumentException("EffectiveToUtc must be later than EffectiveFromUtc.");
            }

            var existing = await repository.FindAsync(rule => rule.Code == code);
            if (existing.Any(rule => PeriodsOverlap(
                    rule.EffectiveFromUtc,
                    rule.EffectiveToUtc,
                    effectiveFrom,
                    effectiveTo)))
            {
                throw new ArgumentException("Tax-rule effective periods for the same code cannot overlap.");
            }

            var rule = new TaxRule
            {
                Code = code,
                Category = request.Category,
                RatePercent = request.RatePercent,
                CalculationMode = request.CalculationMode,
                EffectiveFromUtc = effectiveFrom,
                EffectiveToUtc = effectiveTo,
                IsActive = request.IsActive
            };
            await repository.AddAsync(rule);
            await unitOfWork.SaveChangesAsync();
            return CreatedAtAction(nameof(GetById), new { id = rule.Id },
                ApiResponse<TaxRuleResponse>.CreateSuccess(ToResponse(rule)));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(ApiResponse<object>.CreateFailure(exception.Message));
        }
    }

    private static string Validate(TaxRuleRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Code);
        var code = request.Code.Trim();
        if (code.Length > 64) throw new ArgumentException("Tax-rule code cannot exceed 64 characters.");
        if (!Enum.IsDefined(request.Category)) throw new ArgumentException("Tax category is invalid.");
        if (!Enum.IsDefined(request.CalculationMode)) throw new ArgumentException("Tax calculation mode is invalid.");
        if (request.RatePercent is < 0 or > 100) throw new ArgumentException("Tax rate must be between 0 and 100 percent.");
        if (request.Category is TaxCategory.ZeroRated or TaxCategory.Exempt && request.RatePercent != 0m)
        {
            throw new ArgumentException("Zero-rated and exempt tax rules must use a zero rate.");
        }
        return code;
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static bool PeriodsOverlap(
        DateTime existingFrom,
        DateTime? existingTo,
        DateTime requestedFrom,
        DateTime? requestedTo) =>
        existingFrom < (requestedTo ?? DateTime.MaxValue) &&
        requestedFrom < (existingTo ?? DateTime.MaxValue);

    private static TaxRuleResponse ToResponse(TaxRule rule) => new(
        rule.Id,
        rule.TenantId,
        rule.Code,
        rule.Category,
        rule.RatePercent,
        rule.CalculationMode,
        rule.EffectiveFromUtc,
        rule.EffectiveToUtc,
        rule.IsActive);
}

public sealed record TaxRuleRequest(
    string Code,
    TaxCategory Category,
    decimal RatePercent,
    TaxCalculationMode CalculationMode,
    DateTime EffectiveFromUtc,
    DateTime? EffectiveToUtc,
    bool IsActive = true);

public sealed record TaxRuleResponse(
    int Id,
    string TenantId,
    string Code,
    TaxCategory Category,
    decimal RatePercent,
    TaxCalculationMode CalculationMode,
    DateTime EffectiveFromUtc,
    DateTime? EffectiveToUtc,
    bool IsActive);
