namespace Merconiq.Core.Entities;

/// <summary>Immutable approved forward reversal of an opening-stock baseline.</summary>
public sealed class OpeningStockCorrection : AuditableEntity
{
    public int Id { get; set; }
    public int OpeningStockImportId { get; set; }
    public OpeningStockImport OpeningStockImport { get; set; } = null!;
    public string CorrectionReference { get; set; } = string.Empty;
    public string ApprovalReference { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string CorrectedBy { get; set; } = string.Empty;
    public DateTime CorrectedAt { get; set; }
    public int LineCount { get; set; }
}
