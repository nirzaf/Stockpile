namespace InventoryManagementSystem.Core.Entities;

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
    Sell
}
