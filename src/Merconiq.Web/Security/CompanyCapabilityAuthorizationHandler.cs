using Merconiq.Core.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Merconiq.Web.Security;

public sealed record CompanyCapabilityRequirement(CompanyCapability Capability) : IAuthorizationRequirement;
public sealed record TenantAdministratorRequirement : IAuthorizationRequirement;

/// <summary>Requires a live company grant for capability policies; tenant Admin is explicitly global.</summary>
public sealed class CompanyCapabilityAuthorizationHandler(
    ICurrentUserAuthorization authorization) : AuthorizationHandler<CompanyCapabilityRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CompanyCapabilityRequirement requirement)
    {
        if (await authorization.HasCompanyCapabilityAsync(context.User, requirement.Capability))
        {
            context.Succeed(requirement);
        }
    }
}

public sealed class TenantAdministratorAuthorizationHandler(
    ICurrentUserAuthorization authorization) : AuthorizationHandler<TenantAdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantAdministratorRequirement requirement)
    {
        if (await authorization.IsTenantAdministratorAsync(context.User))
        {
            context.Succeed(requirement);
        }
    }
}
