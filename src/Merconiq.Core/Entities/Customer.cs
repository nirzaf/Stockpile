namespace Merconiq.Core.Entities;

/// <summary>A customer master record owned by one tenant company.</summary>
public sealed class Customer : AuditableEntity
{
    public int Id { get; set; }

    /// <summary>The legal or trading company that owns this customer record.</summary>
    public int CompanyId { get; set; }

    /// <summary>Company-local identifier, stored trimmed and uppercase.</summary>
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>Customer organization or trading name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional operational email address.</summary>
    public string? ContactEmail { get; set; }

    /// <summary>Optional operational phone number.</summary>
    public string? ContactPhone { get; set; }

    /// <summary>Optional billing postal address.</summary>
    public string? BillingAddress { get; set; }

    /// <summary>Optional shipping postal address.</summary>
    public string? ShippingAddress { get; set; }

    /// <summary>
    /// Optional configured payment-term duration. This is master-data configuration only;
    /// it does not calculate due dates or create document snapshots.
    /// </summary>
    public int? PaymentTermDays { get; set; }

    /// <summary>Whether this customer may be selected for future sales documents.</summary>
    public bool IsActive { get; set; } = true;

    public Company Company { get; set; } = null!;
}
