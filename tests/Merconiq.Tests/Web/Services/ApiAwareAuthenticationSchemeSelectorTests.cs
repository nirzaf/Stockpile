using FluentAssertions;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace Merconiq.Tests.Web.Services;

public sealed class ApiAwareAuthenticationSchemeSelectorTests
{
    [Fact]
    public void Selects_bearer_for_API_policy_before_rate_limiting()
    {
        var context = CreateContext("Api");

        ApiAwareAuthenticationSchemeSelector.Select(context)
            .Should().Be(JwtBearerDefaults.AuthenticationScheme);
    }

    [Fact]
    public void Selects_bearer_for_API_routes_without_authorize_metadata()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/items";

        ApiAwareAuthenticationSchemeSelector.Select(context)
            .Should().Be(JwtBearerDefaults.AuthenticationScheme);
    }

    [Fact]
    public void Preserves_application_cookie_for_non_API_policies()
    {
        var context = CreateContext("View");

        ApiAwareAuthenticationSchemeSelector.Select(context)
            .Should().Be(IdentityConstants.ApplicationScheme);
    }

    private static DefaultHttpContext CreateContext(string policy)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(
            static _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute { Policy = policy }),
            "rate-limit-authentication-test"));
        return context;
    }
}
