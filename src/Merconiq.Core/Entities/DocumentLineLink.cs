namespace Merconiq.Core.Entities;

/// <summary>
/// Directed, same-tenant and same-company traceability between document lines.
/// </summary>
public sealed class DocumentLineLink : AuditableEntity
{
    public long Id { get; set; }
    public int CompanyId { get; set; }
    public DocumentIdentityId SourceDocumentId { get; set; }
    public DocumentLineIdentityId SourceLineId { get; set; }
    public DocumentIdentityId TargetDocumentId { get; set; }
    public DocumentLineIdentityId TargetLineId { get; set; }
    public DocumentLineRelationshipType RelationshipType { get; set; }
    public DocumentLineIdentity SourceLine { get; set; } = null!;
    public DocumentLineIdentity TargetLine { get; set; } = null!;
}
