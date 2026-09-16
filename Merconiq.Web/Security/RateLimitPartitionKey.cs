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
        var client = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("client_id")
            ?? context.Request.Headers["X-Client-Id"].FirstOrDefault()
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.Trim())));
        return $"{tenant}:{digest}";
    }
}
