using Microsoft.Extensions.Options;

namespace Merconiq.Web.Tenancy;

/// <summary>Resolves tenants from the deployment's exact host-to-tenant allow-list.</summary>
public sealed class HostTenantResolver(IOptions<TenantOptions> options)
{
    private readonly TenantOptions _options = options.Value;

    public string? Resolve(string host)
    {
        var normalizedHost = host.Trim().TrimEnd('.');
        if (normalizedHost.Length == 0)
        {
            return null;
        }

        foreach (var binding in _options.HostTenants)
        {
            if (string.Equals(binding.Key.Trim().TrimEnd('.'), normalizedHost, StringComparison.OrdinalIgnoreCase))
            {
                return IsValidTenantId(binding.Value) ? binding.Value : null;
            }
        }

        return null;
    }

    private static bool IsValidTenantId(string? tenantId)
    {
        return !string.IsNullOrWhiteSpace(tenantId)
            && tenantId.Length <= 64
            && tenantId.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
    }
}
