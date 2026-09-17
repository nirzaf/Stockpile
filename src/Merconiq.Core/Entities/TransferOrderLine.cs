namespace Merconiq.Core.Entities;

/// <summary>One item/lot obligation in a transfer order.</summary>
public sealed class TransferOrderLine : AuditableEntity
{
    public TransferOrderLine()
    {
        DocumentLineId = DocumentLineIdentityId.New();
    }

    public int Id { get; set; }
    public DocumentLineIdentityId DocumentLineId { get; private set; }
    public DocumentLineIdentity DocumentLineIdentity { get; set; } = null!;
    public int TransferOrderId { get; set; }
    public TransferOrder TransferOrder { get; set; } = null!;
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public int Quantity { get; set; }
    public int DispatchedQuantity { get; set; }
    public string? BatchNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }

    /// <summary>Changes the reservation identity after an approved amendment releases the old reservation.</summary>
    public int ReservationVersion { get; private set; } = 1;

    /// <summary>PostgreSQL row version used to serialize dispatch against amendments.</summary>
    public uint Version { get; private set; }

    public string ReservationSourceLineReference =>
        $"TransferOrder:{DocumentLineId.Value:N}:v{ReservationVersion}";

    public void SetReservationVersionForAmendment() =>
        ReservationVersion = checked(ReservationVersion + 1);
}
