namespace Merconiq.Core.Entities;

/// <summary>
/// Immutable, company-owned in-transit position created by one transfer-order dispatch.
/// The captured value is the source warehouse's moving-average carrying value at dispatch.
/// </summary>
public sealed class TransferTransitEntry : AuditableEntity
{
    public int Id { get; set; }
    public int TransferOrderId { get; set; }
    public int TransferOrderLineId { get; set; }
    public DocumentLineIdentityId SourceDocumentLineId { get; set; }
    public int CompanyId { get; set; }
    public int ItemId { get; set; }
    public int FromLocationId { get; set; }
    public int ToLocationId { get; set; }
    public int StockTransactionId { get; set; }
    public int Quantity { get; set; }
    public string? BatchNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TotalValue { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public string DispatchedBy { get; set; } = null!;
    public DateTimeOffset DispatchedAt { get; set; }
}
