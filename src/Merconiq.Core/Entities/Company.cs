namespace Merconiq.Core.Entities;

/// <summary>Legal or trading company owned by one tenant.</summary>
public sealed class Company : AuditableEntity
{
    public int Id { get; set; }
    /// <summary>Stable identifier used by controlled master-data imports.</summary>
    public string? ExternalId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string? TradingName { get; set; }
    public string? RegistrationNumber { get; set; }
    public string? TaxIdentifier { get; set; }
    public string BaseCurrency { get; set; } = string.Empty;
    /// <summary>Number of fractional decimal places used for company money rounding.</summary>
    public int? CurrencyScale { get; set; }
    public string? CountryCode { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<Branch> Branches { get; set; } = new List<Branch>();
    public ICollection<CompanyMembership> Memberships { get; set; } = new List<CompanyMembership>();
}
