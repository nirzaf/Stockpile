namespace Merconiq.Core.Entities;

/// <summary>Legal or trading company owned by one tenant.</summary>
public sealed class Company : AuditableEntity
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string LegalName { get; set; } = string.Empty;
    public string? TradingName { get; set; }
    public string? RegistrationNumber { get; set; }
    public string? TaxIdentifier { get; set; }
    public string BaseCurrency { get; set; } = "QAR";
    public string? CountryCode { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<Branch> Branches { get; set; } = new List<Branch>();
}
