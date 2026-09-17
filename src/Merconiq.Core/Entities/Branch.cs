namespace Merconiq.Core.Entities;

/// <summary>Operating branch owned by one company and tenant.</summary>
public sealed class Branch : AuditableEntity
{
    public int Id { get; set; }
    /// <summary>Stable identifier used by controlled master-data imports.</summary>
    public string? ExternalId { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public bool IsActive { get; set; } = true;
    public ICollection<Location> Locations { get; set; } = new List<Location>();
}
