namespace Merconiq.Core.Entities;

/// <summary>
/// Represents a purchase order placed with a supplier. Maps to the <c>PurchaseOrders</c> table.
/// </summary>
public class PurchaseOrder : AuditableEntity
{
    public PurchaseOrder()
    {
        DocumentId = DocumentIdentityId.New();
    }

    public int Id { get; set; }

    /// <summary>Immutable internal identity, distinct from the human-facing PO number.</summary>
    public DocumentIdentityId DocumentId { get; private set; }
    public DocumentIdentity DocumentIdentity { get; set; } = null!;

    /// <summary>Business purchase order number (e.g. <c>PO-2026-0001</c>).</summary>
    public string PONumber { get; set; } = string.Empty;

    /// <summary>Date the purchase order was raised.</summary>
    public DateTime OrderDate { get; set; }

    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    /// <summary>Monetary total of all line items.</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>Rounded subtotal before tax after line discounts.</summary>
    public decimal NetAmount { get; set; }

    /// <summary>Total line discounts.</summary>
    public decimal DiscountAmount { get; set; }

    /// <summary>Total tax amount.</summary>
    public decimal TaxAmount { get; set; }

    /// <summary>Currency precision used by all lines in this document.</summary>
    public int CurrencyScale { get; set; } = 2;

    /// <summary>Calculation-policy version used for the persisted document snapshot.</summary>
    public int CalculationVersion { get; set; }

    /// <summary>Monotonically increasing version of the commercial terms.</summary>
    public int CommercialVersion { get; set; } = 1;

    /// <summary>Commercial version captured by the most recent approval.</summary>
    public int? ApprovedCommercialVersion { get; set; }

    /// <summary>JSON snapshot of the commercial terms at the most recent approval.</summary>
    public string? ApprovedCommercialSnapshotJson { get; set; }

    /// <summary>Supplier-agreed delivery terms captured with the order.</summary>
    public string? DeliveryTerms { get; set; }

    /// <summary>PostgreSQL row version used to reject concurrent PO status/amendment writes.</summary>
    public uint Version { get; private set; }

    /// <summary>Monotonic revision advanced with every line-obligation progress change.</summary>
    public int ReceivingRevision { get; private set; }

    /// <summary>Current lifecycle status of the purchase order.</summary>
    public PurchaseOrderStatus Status { get; set; }

    /// <summary>Optional free-text notes.</summary>
    public string? Notes { get; set; }

    public ICollection<OrderDetail> OrderDetails { get; set; } = new List<OrderDetail>();

    /// <summary>Advances the parent row whenever line progress changes, serializing progress with PO lifecycle writes.</summary>
    public void AdvanceReceivingRevision() => ReceivingRevision = checked(ReceivingRevision + 1);
}
