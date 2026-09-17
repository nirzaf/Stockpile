using Merconiq.Core.Services;

namespace Merconiq.Core.Entities;

/// <summary>
/// Tenant-scoped, effective-dated tax policy. The selected values are copied to a document
/// line when it is calculated; later rule edits cannot change that snapshot.
/// </summary>
public sealed class TaxRule : AuditableEntity
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public TaxCategory Category { get; set; }
    public decimal RatePercent { get; set; }
    public TaxCalculationMode CalculationMode { get; set; }
    public DateTime EffectiveFromUtc { get; set; }
    public DateTime? EffectiveToUtc { get; set; }
    public bool IsActive { get; set; } = true;
}
