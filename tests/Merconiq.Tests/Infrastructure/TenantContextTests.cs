using FluentAssertions;
using Merconiq.Web.Tenancy;
using Microsoft.Extensions.Options;

namespace Merconiq.Tests.Infrastructure;

public class TenantContextTests
{
    [Fact]
    public void Context_is_unresolved_until_explicitly_set()
    {
        var context = new TenantContext();

        context.IsResolved.Should().BeFalse();
        var act = () => context.TenantId;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Resolver_returns_the_configured_tenant_for_an_exact_host()
    {
        var resolver = new HostTenantResolver(Options.Create(new TenantOptions
        {
            HostTenants = new Dictionary<string, string>
            {
                ["tenant-a.example"] = "tenant-a"
            }
        }));

        resolver.Resolve("tenant-a.example.").Should().Be("tenant-a");
    }

    [Fact]
    public void Resolver_rejects_an_unconfigured_host()
    {
        var resolver = new HostTenantResolver(Options.Create(new TenantOptions
        {
            HostTenants = new Dictionary<string, string>
            {
                ["tenant-a.example"] = "tenant-a"
            }
        }));

        resolver.Resolve("tenant-b.example").Should().BeNull();
    }

    [Fact]
    public void Context_cannot_be_switched_to_another_tenant()
    {
        var context = new TenantContext();

        context.SetTenant("tenant-a");

        var act = () => context.SetTenant("tenant-b");

        act.Should().Throw<InvalidOperationException>();
        context.TenantId.Should().Be("tenant-a");
    }
}
