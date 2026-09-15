using InventoryManagementSystem.Core.Interfaces;

namespace InventoryManagementSystem.Tests.Infrastructure;

internal sealed class TestTenantContext(string tenantId) : ITenantContext
{
    public string TenantId { get; } = tenantId;

    public bool IsResolved => true;
}
