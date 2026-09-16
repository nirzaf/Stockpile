namespace Merconiq.Core.Entities;

/// <summary>Lifecycle state of a stock reservation.</summary>
public enum StockReservationStatus
{
    Active,
    Released,
    Consumed,
    Expired,
    Cancelled
}
