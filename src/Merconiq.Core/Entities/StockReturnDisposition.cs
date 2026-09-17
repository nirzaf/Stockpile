namespace Merconiq.Core.Entities;

/// <summary>Operational disposition for stock returned against an original sale.</summary>
public enum StockReturnDisposition
{
    /// <summary>Returned goods are eligible for normal available stock.</summary>
    Restockable,

    /// <summary>Returned goods are held out of availability because they are damaged.</summary>
    Damaged,

    /// <summary>Returned goods are held out of availability pending quarantine handling.</summary>
    Quarantined
}
