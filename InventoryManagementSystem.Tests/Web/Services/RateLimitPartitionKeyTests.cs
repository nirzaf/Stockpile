using System.Net;
using System.Security.Claims;
using FluentAssertions;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryManagementSystem.Tests.Web.Services;

public class RateLimitPartitionKeyTests
{
    [Fact]
    public void Separates_tenants_and_clients()
    {
        var tenantA = CreateContext("tenant-a", "client-1");
        var tenantB = CreateContext("tenant-b", "client-1");
        var clientB = CreateContext("tenant-a", "client-2");

        RateLimitPartitionKey.ForApi(tenantA).Should().NotBe(RateLimitPartitionKey.ForApi(tenantB));
        RateLimitPartitionKey.ForApi(tenantA).Should().NotBe(RateLimitPartitionKey.ForApi(clientB));
    }

    [Fact]
    public void Uses_remote_ip_for_anonymous_callers()
    {
        var context = new DefaultHttpContext
        {
            Connection = { RemoteIpAddress = IPAddress.Parse("203.0.113.10") }
        };
        context.RequestServices = new ServiceCollection()
            .AddScoped<ITenantContext>(_ => new StubTenantContext("tenant-a"))
            .BuildServiceProvider();

        RateLimitPartitionKey.ForApi(context).Should().Contain("tenant-a:");
    }

    private static DefaultHttpContext CreateContext(string tenant, string client)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, client)
        ], "test"));
        context.RequestServices = new ServiceCollection()
            .AddScoped<ITenantContext>(_ => new StubTenantContext(tenant))
            .BuildServiceProvider();
        return context;
    }

    private sealed class StubTenantContext(string tenant) : ITenantContext
    {
        public string TenantId { get; } = tenant;
        public bool IsResolved => true;

        public void SetTenant(string tenantId) => throw new NotSupportedException();
    }
}
