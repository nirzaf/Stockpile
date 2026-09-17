namespace Merconiq.Core.Entities;

/// <summary>Lifecycle of a controlled same-company transfer order.</summary>
public enum TransferOrderStatus
{
    Draft,
    Approved,
    InTransit,
    PartiallyReceived,
    Completed,
    Cancelled
}
