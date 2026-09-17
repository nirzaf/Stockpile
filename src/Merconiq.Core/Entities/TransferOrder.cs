namespace Merconiq.Core.Entities;

/// <summary>Numbered, pre-approved stock transfer between two locations in one company.</summary>
public sealed class TransferOrder : AuditableEntity
{
    public TransferOrder()
    {
        DocumentId = DocumentIdentityId.New();
        OrderDate = DateTime.UtcNow;
    }

    public int Id { get; set; }
    public DocumentIdentityId DocumentId { get; private set; }
    public DocumentIdentity DocumentIdentity { get; set; } = null!;
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public int FromLocationId { get; set; }
    public Location FromLocation { get; set; } = null!;
    public int ToLocationId { get; set; }
    public Location ToLocation { get; set; } = null!;
    public DateTime OrderDate { get; set; }
    public TransferOrderStatus Status { get; set; } = TransferOrderStatus.Draft;
    public string? Notes { get; set; }

    /// <summary>PostgreSQL row version used to reject concurrent lifecycle changes.</summary>
    public uint Version { get; private set; }

    public ICollection<TransferOrderLine> Lines { get; set; } = new List<TransferOrderLine>();

    public void AttachDocumentIdentity(DocumentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        DocumentId = identity.Id;
        DocumentIdentity = identity;
    }
}
