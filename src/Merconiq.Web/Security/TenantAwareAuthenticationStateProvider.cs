using Merconiq.Core.Interfaces;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Merconiq.Web.Security;

/// <summary>Revalidates interactive-server sessions against current tenant-scoped Identity data.</summary>
public sealed class TenantAwareAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        var principal = authenticationState.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            return true;
        }

        var tenantId = principal.FindFirst("tenant_id")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        if (!tenantContext.IsResolved)
        {
            tenantContext.SetTenant(tenantId);
        }
        else if (!string.Equals(tenantContext.TenantId, tenantId, StringComparison.Ordinal))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var authorization = scope.ServiceProvider.GetRequiredService<CurrentUserAuthorization>();
        return await authorization.IsSessionCurrentAsync(principal);
    }
}
