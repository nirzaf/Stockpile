using Merconiq.Core.Services;

namespace Merconiq.Core.Entities;

/// <summary>
/// Represents a single line item on a purchase order. Maps to the <c>OrderDetails</c> table.
/// </summary>
public class OrderDetail : AuditableEntity
{
    public OrderDetail()
    {
        DocumentLineId = DocumentLineIdentityId.New();
    }

    public int Id { get; set; }
    /// <summary>Immutable identity for this line, independent of its database row number.</summary>
    public DocumentLineIdentityId DocumentLineId { get; private set; }
    public DocumentLineIdentity? DocumentLineIdentity { get; set; }
    public int PurchaseOrderId { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = null!;
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    /// <summary>Quantity of the item ordered on this line.</summary>
    public int Quantity { get; set; }

    /// <summary>Per-unit price agreed with the supplier.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Optional effective-dated tax rule selected for this line.</summary>
    public int? TaxRuleId { get; set; }
    public TaxRule? TaxRule { get; set; }

    /// <summary>Tax classification captured at calculation time.</summary>
    public TaxCategory TaxCategory { get; set; } = TaxCategory.Standard;

    /// <summary>Tax-inclusive or tax-exclusive pricing captured at calculation time.</summary>
    public TaxCalculationMode TaxMode { get; set; } = TaxCalculationMode.Exclusive;

    /// <summary>Whether this line is a normal charge or a signed reversal.</summary>
    public DocumentLineDirection Direction { get; set; } = DocumentLineDirection.Charge;

    /// <summary>Discount percentage supplied for the line.</summary>
    public decimal DiscountPercent { get; set; }

    /// <summary>Effective tax rate captured at calculation time.</summary>
    public decimal TaxRatePercent { get; set; }

    /// <summary>Currency precision used by the calculation.</summary>
    public int CurrencyScale { get; set; } = 2;

    /// <summary>Effective date of the selected tax rule, if one was used.</summary>
    public DateTime? TaxEffectiveFromUtc { get; set; }

    /// <summary>Calculation-policy version used for the persisted snapshot.</summary>
    public int CalculationVersion { get; set; }

    /// <summary>Rounded amount before tax after discount.</summary>
    public decimal NetAmount { get; set; }

    /// <summary>Rounded discount amount.</summary>
    public decimal DiscountAmount { get; set; }

    /// <summary>Rounded taxable amount.</summary>
    public decimal TaxableAmount { get; set; }

    /// <summary>Rounded tax amount.</summary>
    public decimal TaxAmount { get; set; }

    /// <summary>Rounded final line amount.</summary>
    public decimal GrossAmount { get; set; }

    /// <summary>Computed line total: <c>Quantity * UnitPrice</c>.</summary>
    public decimal TotalPrice => Quantity * UnitPrice;
}
