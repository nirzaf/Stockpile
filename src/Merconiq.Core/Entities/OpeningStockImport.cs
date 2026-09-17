namespace Merconiq.Core.Entities;

/// <summary>Immutable approval and idempotency record for an opening-stock replay.</summary>
public sealed class OpeningStockImport : AuditableEntity
{
    public int Id { get; set; }
    public string ImportReference { get; set; } = string.Empty;
    public string ApprovalReference { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime ApprovedAt { get; set; }
    public DateTime CutoverAt { get; set; }
    public int LineCount { get; set; }

    public ICollection<OpeningStockImportLine> Lines { get; set; } = new List<OpeningStockImportLine>();
}
