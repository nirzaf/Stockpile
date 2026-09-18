using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Merconiq.Core.Interfaces;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Tests.Web.Services;

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

    [Fact]
    public void Anonymous_callers_cannot_rotate_api_partitions_with_untrusted_claims_or_headers()
    {
        var first = CreateAnonymousApiContext("tenant-a", "203.0.113.10", "spoofed-user-a", "spoofed-client-a");
        var second = CreateAnonymousApiContext("tenant-a", "203.0.113.10", "spoofed-user-b", "spoofed-client-b");
        var otherAddress = CreateAnonymousApiContext("tenant-a", "203.0.113.11", "spoofed-user-a", "spoofed-client-a");

        RateLimitPartitionKey.ForApi(first).Should().Be(RateLimitPartitionKey.ForApi(second));
        RateLimitPartitionKey.ForApi(first).Should().NotBe(RateLimitPartitionKey.ForApi(otherAddress));
    }

    [Fact]
    public void Ignores_rate_limit_claims_from_unauthenticated_identities()
    {
        var first = CreateApiContextWithMixedIdentities("tenant-a", "203.0.113.10", "spoofed-user-a");
        var second = CreateApiContextWithMixedIdentities("tenant-a", "203.0.113.10", "spoofed-user-b");

        RateLimitPartitionKey.ForApi(first).Should().Be(RateLimitPartitionKey.ForApi(second));
    }

    [Fact]
    public void Login_partition_is_tenant_scoped_and_uses_remote_ip()
    {
        var tenantA = CreateLoginContext("tenant-a", "203.0.113.10");
        var tenantB = CreateLoginContext("tenant-b", "203.0.113.10");

        RateLimitPartitionKey.ForLogin(tenantA).Should().NotBe(RateLimitPartitionKey.ForLogin(tenantB));
        RateLimitPartitionKey.ForLogin(tenantA).Should().Contain("tenant-a:");
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

    private static DefaultHttpContext CreateLoginContext(string tenant, string remoteIp)
    {
        var context = new DefaultHttpContext
        {
            Connection = { RemoteIpAddress = IPAddress.Parse(remoteIp) }
        };
        context.RequestServices = new ServiceCollection()
            .AddScoped<ITenantContext>(_ => new StubTenantContext(tenant))
            .BuildServiceProvider();
        return context;
    }

    private static DefaultHttpContext CreateAnonymousApiContext(
        string tenant,
        string remoteIp,
        string untrustedUserId,
        string untrustedClientId)
    {
        var context = new DefaultHttpContext
        {
            Connection = { RemoteIpAddress = IPAddress.Parse(remoteIp) }
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, untrustedUserId)
        ]));
        context.Request.Headers["X-Client-Id"] = untrustedClientId;
        context.RequestServices = new ServiceCollection()
            .AddScoped<ITenantContext>(_ => new StubTenantContext(tenant))
            .BuildServiceProvider();
        return context;
    }

    private static DefaultHttpContext CreateApiContextWithMixedIdentities(
        string tenant,
        string remoteIp,
        string untrustedUserId)
    {
        var context = new DefaultHttpContext
        {
            Connection = { RemoteIpAddress = IPAddress.Parse(remoteIp) }
        };
        context.User = new ClaimsPrincipal([
            new ClaimsIdentity([], "authenticated-scheme"),
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, untrustedUserId)])
        ]);
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
