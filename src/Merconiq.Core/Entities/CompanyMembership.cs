namespace Merconiq.Core.Entities;

/// <summary>Tenant-safe capability grants for one user in one company.</summary>
public sealed class CompanyMembership : AuditableEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
    public CompanyCapability Capabilities { get; set; }
    public bool IsActive { get; set; } = true;
}
