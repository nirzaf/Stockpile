namespace Merconiq.Core.Entities;

/// <summary>Immutable quantity and value accepted from one dispatched transit entry.</summary>
public sealed class TransferTransitReceipt : AuditableEntity
{
    public int Id { get; set; }
    public int TransferTransitEntryId { get; set; }
    public int StockTransactionId { get; set; }
    public StockTransaction StockTransaction { get; set; } = null!;
    public int Quantity { get; set; }
    public int RemainingQuantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TotalValue { get; set; }
    public decimal RemainingValue { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public string ReceivedBy { get; set; } = null!;
    public DateTimeOffset ReceivedAt { get; set; }
}
