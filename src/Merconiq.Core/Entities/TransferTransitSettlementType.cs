namespace Merconiq.Core.Entities;

/// <summary>Immutable resolution recorded against dispatched transit quantity.</summary>
public enum TransferTransitSettlementType
{
    Received,
    Quarantined,
    Returned,
    /// <summary>Approved transit-only disposition with no stock movement or GL posting.</summary>
    WrittenOff
}
