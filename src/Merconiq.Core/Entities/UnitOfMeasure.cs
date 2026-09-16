using Merconiq.Core.Interfaces;

namespace Merconiq.Core.Entities;

/// <summary>Tenant-scoped unit used for item quantities and conversions.</summary>
public sealed class UnitOfMeasure : AuditableEntity, ISoftDelete
{
    public int Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int DecimalPlaces { get; set; }
    public bool IsWholeUnitOnly { get; set; }
    public bool IsDeleted { get; set; }
}
