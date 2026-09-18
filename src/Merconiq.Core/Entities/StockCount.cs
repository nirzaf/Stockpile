namespace Merconiq.Core.Entities;

/// <summary>Immutable physical-count baseline for one tenant location.</summary>
public sealed class StockCount : AuditableEntity
{
    public int Id { get; set; }
    public int LocationId { get; set; }
    public Location Location { get; set; } = null!;
    public int? CompanyId { get; set; }
    public Company? Company { get; set; }
    public DateTime SnapshotAtUtc { get; set; }

    /// <summary>Highest movement identity visible when the location snapshot was captured.</summary>
    public int MovementWatermark { get; set; }

    public ICollection<StockCountLine> Lines { get; set; } = new List<StockCountLine>();
}
