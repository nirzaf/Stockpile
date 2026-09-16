namespace Merconiq.Core.Entities;

/// <summary>Moving-average quantity and value for one tenant/item/location bucket.</summary>
public sealed class StockValuationBucket : AuditableEntity
{
    public int Id { get; set; }
    public int ItemId { get; set; }
    public Item Item { get; set; } = null!;
    public int LocationId { get; set; }
    public Location Location { get; set; } = null!;
    public int Quantity { get; set; }
    public decimal Value { get; set; }

    /// <summary>PostgreSQL xmin value used for optimistic concurrency checks.</summary>
    public uint Version { get; set; }
}
