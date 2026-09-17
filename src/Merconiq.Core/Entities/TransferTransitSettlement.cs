namespace Merconiq.Core.Entities;

/// <summary>
/// Immutable, company-owned resolution of a dispatched in-transit quantity.
/// </summary>
public sealed class TransferTransitSettlement : AuditableEntity
{
    public int Id { get; set; }
    public int TransferTransitEntryId { get; set; }
    public int TransferOrderId { get; set; }
    public int TransferOrderLineId { get; set; }
    public DocumentLineIdentityId SourceDocumentLineId { get; set; }
    public int CompanyId { get; set; }
    public int ItemId { get; set; }
    public int FromLocationId { get; set; }
    public int ToLocationId { get; set; }
    public int StockTransactionId { get; set; }
    public int Quantity { get; set; }
    public TransferTransitSettlementType SettlementType { get; set; }
    public string? BatchNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TotalValue { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public string SettledBy { get; set; } = null!;
    public DateTimeOffset SettledAt { get; set; }
    public string? Reason { get; set; }
}
