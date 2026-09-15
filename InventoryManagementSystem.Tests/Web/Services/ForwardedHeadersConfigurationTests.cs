using System.Net;
using FluentAssertions;
using InventoryManagementSystem.Web.Security;
using Microsoft.AspNetCore.HttpOverrides;

namespace InventoryManagementSystem.Tests.Web.Services;

public class ForwardedHeadersConfigurationTests
{
    [Fact]
    public void Accepts_only_configured_proxy_addresses_and_networks()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:TrustedProxies:0"] = "192.0.2.10",
                ["Security:TrustedNetworks:0"] = "198.51.100.0/24"
            })
            .Build();
        var options = new ForwardedHeadersOptions();

        ForwardedHeadersConfiguration.Configure(options, configuration);

        options.KnownProxies.Should().Contain(IPAddress.Parse("192.0.2.10"));
        options.KnownNetworks.Should().Contain(network => network.Prefix.Equals(IPAddress.Parse("198.51.100.0")) && network.PrefixLength == 24);
        options.ForwardLimit.Should().Be(1);
    }
}
