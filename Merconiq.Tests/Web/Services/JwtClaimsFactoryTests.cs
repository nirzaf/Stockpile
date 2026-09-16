using System.Security.Claims;
using FluentAssertions;
using Merconiq.Core.Entities;
using Merconiq.Web.Security;

namespace Merconiq.Tests.Web.Services;

public class JwtClaimsFactoryTests
{
    [Fact]
    public void Emits_all_distinct_roles()
    {
        var user = new ApplicationUser { Id = "user-1", UserName = "person@example.com" };

        var claims = JwtClaimsFactory.Create(user, "tenant-a", ["Admin", "Manager", "Admin"]);

        claims.Where(claim => claim.Type == ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Should().Equal("Admin", "Manager");
    }

    [Fact]
    public void Supplies_staff_fallback_when_user_has_no_roles()
    {
        var user = new ApplicationUser { Id = "user-1", UserName = "person@example.com" };

        JwtClaimsFactory.Create(user, "tenant-a", [])
            .Single(claim => claim.Type == ClaimTypes.Role)
            .Value.Should().Be("Staff");
    }
}
