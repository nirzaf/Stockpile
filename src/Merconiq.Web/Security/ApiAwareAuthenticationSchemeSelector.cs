using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace Merconiq.Web.Security;

/// <summary>Selects the API bearer identity before rate limiting while preserving browser cookies elsewhere.</summary>
public static class ApiAwareAuthenticationSchemeSelector
{
    public const string SchemeName = "ApplicationOrApi";

    public static string Select(HttpContext context)
    {
        var usesApiPolicy = context.GetEndpoint()?.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Any(data => string.Equals(data.Policy, "Api", StringComparison.Ordinal)) == true;
        var isApiRoute = context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

        return usesApiPolicy || isApiRoute
            ? JwtBearerDefaults.AuthenticationScheme
            : IdentityConstants.ApplicationScheme;
    }
}
