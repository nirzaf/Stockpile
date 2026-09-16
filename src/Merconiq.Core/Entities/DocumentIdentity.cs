namespace Merconiq.Core.Entities;

/// <summary>
/// Durable identity and human number for a business document. Legacy purchase orders can
/// remain company-unassigned until their owner completes the company mapping.
/// </summary>
public sealed class DocumentIdentity : AuditableEntity
{
    private DocumentIdentity()
    {
    }

    public DocumentIdentityId Id { get; private set; }
    public int? CompanyId { get; private set; }
    public Company? Company { get; private set; }
    public string DocumentType { get; private set; } = string.Empty;
    public string HumanNumber { get; private set; } = string.Empty;
    public int Period { get; private set; }
    public DocumentLifecycleStatus Status { get; private set; }
    public string RequestScope { get; private set; } = string.Empty;
    public string? RequestKey { get; private set; }
    public string? RequestHash { get; private set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public ICollection<DocumentLineIdentity> Lines { get; private set; } = new List<DocumentLineIdentity>();

    public static DocumentIdentity Create(
        DocumentIdentityId id,
        string tenantId,
        int? companyId,
        string documentType,
        string humanNumber,
        int period,
        DocumentLifecycleStatus status,
        string requestScope,
        string? requestKey = null,
        string? requestHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(humanNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestScope);
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("A document identity cannot be empty.", nameof(id));
        }
        if (companyId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(companyId));
        }
        if (documentType.Trim().Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(documentType), "A document type cannot exceed 64 characters.");
        }
        if (humanNumber.Trim().Length > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(humanNumber), "A human document number cannot exceed 50 characters.");
        }
        if (requestScope.Trim().Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(requestScope), "A request scope cannot exceed 256 characters.");
        }
        if (period is < 2000 or > 9999)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if ((requestKey is null) != (requestHash is null))
        {
            throw new ArgumentException("An idempotency key and request hash must be supplied together.");
        }
        if (requestKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requestKey);
        }
        if (requestKey?.Length > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(requestKey), "An idempotency key cannot exceed 200 characters.");
        }
        if (requestHash is not null &&
            (requestHash.Length != 64 || requestHash.Any(character => !Uri.IsHexDigit(character))))
        {
            throw new ArgumentException("An idempotency request hash must be a SHA-256 hexadecimal value.", nameof(requestHash));
        }

        return new DocumentIdentity
        {
            Id = id,
            TenantId = tenantId,
            CompanyId = companyId,
            DocumentType = documentType.Trim(),
            HumanNumber = humanNumber.Trim(),
            Period = period,
            Status = status,
            RequestScope = requestScope.Trim(),
            RequestKey = requestKey,
            RequestHash = requestHash
        };
    }

    /// <summary>Transitions without deleting the number or its audit/lineage record.</summary>
    public void TransitionTo(DocumentLifecycleStatus status)
    {
        if (Status == status)
        {
            return;
        }

        var allowed = Status switch
        {
            DocumentLifecycleStatus.Draft => status is DocumentLifecycleStatus.Active
                or DocumentLifecycleStatus.Cancelled
                or DocumentLifecycleStatus.Voided,
            DocumentLifecycleStatus.Active => status is DocumentLifecycleStatus.Cancelled
                or DocumentLifecycleStatus.Voided,
            _ => false
        };

        if (!allowed)
        {
            throw new InvalidOperationException($"Invalid document lifecycle transition: {Status} -> {status}.");
        }

        Status = status;
    }
}
