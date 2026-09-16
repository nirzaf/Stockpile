namespace Merconiq.Core.Interfaces;

/// <summary>Marks data that belongs to one isolated tenant.</summary>
public interface ITenantScoped
{
    /// <summary>Stable tenant identifier assigned from the trusted tenant context.</summary>
    string TenantId { get; set; }
}
