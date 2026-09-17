namespace Merconiq.Core.Entities;

/// <summary>A lot-specific part of a source-line stock reservation.</summary>
public sealed class StockReservationAllocation : AuditableEntity
{
    public int Id { get; set; }
    public int ReservationId { get; set; }
    public StockReservation Reservation { get; set; } = null!;
    public int Ordinal { get; set; }
    public string? BatchNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public int Quantity { get; set; }
    public int ConsumedQuantity { get; set; }
    public string? ExpiryExceptionReason { get; set; }
    public uint Version { get; set; }
}
