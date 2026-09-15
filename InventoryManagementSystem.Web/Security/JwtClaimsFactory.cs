using System.Security.Claims;
using InventoryManagementSystem.Core.Entities;

namespace InventoryManagementSystem.Web.Security;

/// <summary>Builds the stable claims shared by API JWT creation and its tests.</summary>
public static class JwtClaimsFactory
{
    public static IReadOnlyList<Claim> Create(ApplicationUser user, string tenantId, IEnumerable<string> roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
            new(ClaimTypes.NameIdentifier, user.Id),
            new("tenant_id", tenantId)
        };

        var distinctRoles = roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinctRoles.Length == 0)
        {
            distinctRoles = ["Staff"];
        }

        claims.AddRange(distinctRoles.Select(role => new Claim(ClaimTypes.Role, role)));
        return claims;
    }
}
