namespace Merconiq.Core.Entities;

/// <summary>Stable mapping from a persisted business line to its typed document and line IDs.</summary>
public sealed class DocumentLineIdentity : AuditableEntity
{
    private DocumentLineIdentity()
    {
    }

    public DocumentLineIdentityId Id { get; private set; }
    public DocumentIdentityId DocumentId { get; private set; }
    public int? CompanyId { get; private set; }
    public string LineType { get; private set; } = string.Empty;
    public DocumentIdentity DocumentIdentity { get; private set; } = null!;
    public OrderDetail? OrderDetail { get; set; }

    public static DocumentLineIdentity Create(
        DocumentLineIdentityId id,
        DocumentIdentityId documentId,
        string tenantId,
        int? companyId,
        string lineType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(lineType);
        if (id.Value == Guid.Empty || documentId.Value == Guid.Empty)
        {
            throw new ArgumentException("Document and line identities cannot be empty.");
        }
        if (companyId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(companyId));
        }
        if (lineType.Trim().Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(lineType));
        }

        return new DocumentLineIdentity
        {
            Id = id,
            DocumentId = documentId,
            TenantId = tenantId,
            CompanyId = companyId,
            LineType = lineType.Trim()
        };
    }
}
