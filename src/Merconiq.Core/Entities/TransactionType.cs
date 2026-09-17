namespace Merconiq.Core.Entities;

/// <summary>
/// Types of stock movement transactions.
/// </summary>
public enum TransactionType
{
    /// <summary>Stock received from a supplier into a location.</summary>
    Receive,

    /// <summary>Stock moved between two locations.</summary>
    Transfer,

    /// <summary>Stock sold out of a location.</summary>
    Sell,

    /// <summary>Stock returned against an original sale movement.</summary>
    Return,

    /// <summary>Approved opening-baseline quantity at a cutover instant.</summary>
    Opening,

    /// <summary>Available stock moved into quarantine without changing on-hand quantity.</summary>
    Quarantine,

    /// <summary>Previously quarantined stock made available again.</summary>
    QuarantineRelease,

    /// <summary>Approved reserved stock dispatched from a warehouse into transit.</summary>
    TransferDispatch
}
