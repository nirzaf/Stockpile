namespace Merconiq.Core.Entities;

/// <summary>
/// Represents a historical stock movement (receive, transfer, sell, or return). Maps to the
/// <c>StockTransactions</c> table. Used for audit, reporting, and ML input.
/// </summary>
public class StockTransaction : AuditableEntity
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;

    /// <summary>Source location. For receives, this is the receiving location.</summary>
    public int FromLocationId { get; set; }
    public Location FromLocation { get; set; } = null!;

    /// <summary>Destination location. Null for receives and sells.</summary>
    public int? ToLocationId { get; set; }
    public Location? ToLocation { get; set; }

    /// <summary>Quantity moved.</summary>
    public int Quantity { get; set; }

    /// <summary>The kind of stock movement.</summary>
    public TransactionType TransactionType { get; set; }

    /// <summary>Original sale movement for a source-linked return.</summary>
    public int? OriginalTransactionId { get; set; }
    public StockTransaction? OriginalTransaction { get; set; }

    /// <summary>Stable source line used to prevent duplicate return postings.</summary>
    public string? SourceLineReference { get; set; }

    /// <summary>Disposition recorded for a return movement.</summary>
    public StockReturnDisposition? ReturnDisposition { get; set; }

    /// <summary>Original sale unit cost captured for a valued return.</summary>
    public decimal? UnitCost { get; set; }

    /// <summary>Date and time the movement occurred.</summary>
    public DateTime TransactionDate { get; set; }

    /// <summary>Optional lot/batch number for traceability.</summary>
    public string? BatchNumber { get; set; }

    /// <summary>Optional expiry date for perishable goods.</summary>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>Optional free-text notes.</summary>
    public string? Notes { get; set; }
}
