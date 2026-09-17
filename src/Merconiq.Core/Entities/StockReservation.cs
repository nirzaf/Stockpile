namespace Merconiq.Core.Entities;

/// <summary>One auditable, tenant-scoped reservation for a source document line and lot.</summary>
public sealed class StockReservation : AuditableEntity
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public int LocationId { get; set; }
    public Location Location { get; set; } = null!;
    public string SourceLineReference { get; set; } = string.Empty;
    public string? BatchNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public int Quantity { get; set; }
    public int ConsumedQuantity { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public StockReservationStatus Status { get; set; } = StockReservationStatus.Active;
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ResolutionReason { get; set; }

    /// <summary>Reason recorded when an authorized user reserves stock from an expired lot.</summary>
    public string? ExpiryExceptionReason { get; set; }

    /// <summary>Ordered per-lot quantities assigned to this source line.</summary>
    public ICollection<StockReservationAllocation> Allocations { get; set; } = new List<StockReservationAllocation>();

    /// <summary>PostgreSQL xmin value used for optimistic concurrency checks.</summary>
    public uint Version { get; set; }
}
