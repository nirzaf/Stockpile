using System.Net;
using System.Net.Sockets;

namespace InventoryManagementSystem.Web.Security;

/// <summary>Validates webhook targets against HTTPS and private-network SSRF targets.</summary>
public static class WebhookUrlValidator
{
    public static async Task<string?> ValidateAsync(string? rawUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) || rawUrl.Length > 2048 ||
            !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length > 0)
        {
            return "Url must be an absolute HTTPS URL on port 443 with no user information and no longer than 2048 characters.";
        }

        var host = uri.DnsSafeHost.TrimEnd('.');
        if (string.IsNullOrWhiteSpace(host) || IsBlockedHostName(host))
        {
            return "Url host is not allowed.";
        }

        if (IPAddress.TryParse(host, out var literalAddress))
        {
            return IsPublicAddress(literalAddress) ? null : "Url must not target a loopback, private, link-local, metadata, or otherwise non-public address.";
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        }
        catch (SocketException)
        {
            return "Url host could not be resolved safely.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
        {
            return "Url must resolve only to public addresses.";
        }

        return null;
    }

    private static bool IsBlockedHostName(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("metadata", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None) ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 or 10 or 127 or 169 => false,
                192 when bytes[1] == 0 && bytes[2] == 0 => false,
                192 when bytes[1] == 0 && bytes[2] == 2 => false,
                192 when bytes[1] == 168 => false,
                198 when bytes[1] is 18 or 19 => false,
                203 when bytes[1] == 0 && bytes[2] == 113 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                _ => true
            };
        }

        // fc00::/7 is IPv6 unique-local; fe80::/10 is link-local.
        return (bytes[0] & 0xfe) != 0xfc && !(bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80);
    }
}
