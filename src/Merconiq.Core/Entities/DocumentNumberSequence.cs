namespace Merconiq.Core.Entities;

/// <summary>Tenant/company/document-period counter; cancelled numbers are never reused.</summary>
public sealed class DocumentNumberSequence : AuditableEntity
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public int Period { get; set; }
    public string Prefix { get; set; } = string.Empty;
    public int NextNumber { get; set; } = 1;
    public uint Version { get; set; }
}
