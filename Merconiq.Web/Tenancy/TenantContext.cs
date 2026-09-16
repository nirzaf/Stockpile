using Merconiq.Core.Interfaces;

namespace Merconiq.Web.Tenancy;

/// <summary>Scoped tenant context populated by <see cref="TenantContextMiddleware"/>.</summary>
public sealed class TenantContext : ITenantContext
{
    private string? _tenantId;

    public string TenantId => _tenantId
        ?? throw new InvalidOperationException("A tenant context has not been resolved for this scope.");

    public bool IsResolved => _tenantId is not null;

    /// <summary>Sets the tenant for this request or explicitly-created work scope.</summary>
    public void SetTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("A tenant identifier is required.", nameof(tenantId));
        }

        if (_tenantId is not null && !string.Equals(_tenantId, tenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The tenant context cannot be changed after it is resolved.");
        }

        _tenantId = tenantId;
    }
}
