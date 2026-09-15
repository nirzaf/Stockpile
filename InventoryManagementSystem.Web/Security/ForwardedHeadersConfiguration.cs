using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace InventoryManagementSystem.Web.Security;

/// <summary>Restricts forwarded client/protocol headers to configured edge networks.</summary>
public static class ForwardedHeadersConfiguration
{
    public static void Configure(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedProto |
            ForwardedHeaders.XForwardedHost;
        options.RequireHeaderSymmetry = true;
        options.ForwardLimit = 1;

        foreach (var value in configuration.GetSection("Security:TrustedProxies").Get<string[]>() ?? [])
        {
            if (IPAddress.TryParse(value, out var address))
            {
                options.KnownProxies.Add(address);
            }
        }

        foreach (var value in configuration.GetSection("Security:TrustedNetworks").Get<string[]>() ?? [])
        {
            var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var prefix) &&
                int.TryParse(parts[1], out var prefixLength))
            {
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
            }
        }
    }
}
