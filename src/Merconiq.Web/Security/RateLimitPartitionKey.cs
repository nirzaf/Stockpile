using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Merconiq.Core.Interfaces;

namespace Merconiq.Web.Security;

/// <summary>Derives bounded, non-secret rate-limit partitions for API callers.</summary>
public static class RateLimitPartitionKey
{
    public static string ForApi(HttpContext context)
    {
        var tenant = context.RequestServices.GetService<ITenantContext>()?.TenantId ?? "unresolved";
        var authenticatedIdentity = context.User.Identities.FirstOrDefault(identity => identity.IsAuthenticated);
        var authenticatedClient = authenticatedIdentity?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? authenticatedIdentity?.FindFirst("client_id")?.Value;
        var client = authenticatedClient
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.Trim())));
        return $"{tenant}:{digest}";
    }

    public static string ForLogin(HttpContext context)
    {
        var tenant = context.RequestServices.GetService<ITenantContext>()?.TenantId ?? "unresolved";
        var client = context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.Trim())));
        return $"{tenant}:{digest}";
    }
}
