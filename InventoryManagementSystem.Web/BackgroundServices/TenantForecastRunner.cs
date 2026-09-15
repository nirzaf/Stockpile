using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Web.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryManagementSystem.Web.BackgroundServices;

/// <summary>Runs forecast work inside a scope with an explicitly selected tenant.</summary>
public interface ITenantForecastRunner
{
    Task RunAsync(string tenantId, CancellationToken cancellationToken);
}

public sealed class TenantForecastRunner(IServiceScopeFactory scopeFactory) : ITenantForecastRunner
{
    public async Task RunAsync(string tenantId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenantContext.SetTenant(tenantId);

        var forecastService = scope.ServiceProvider.GetRequiredService<IDemandForecastService>();
        await forecastService.ForecastAllItemsAsync(30);
    }
}
