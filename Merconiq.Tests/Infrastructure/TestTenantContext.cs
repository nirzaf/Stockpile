using Merconiq.Core.Interfaces;

namespace Merconiq.Tests.Infrastructure;

internal sealed class TestTenantContext(string tenantId) : ITenantContext
{
    private string _tenantId = tenantId;

    public string TenantId => _tenantId;

    public bool IsResolved => true;

    public void SetTenant(string tenantId)
    {
        if (!string.Equals(_tenantId, tenantId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The test tenant context cannot be switched.");
        }
    }
}
