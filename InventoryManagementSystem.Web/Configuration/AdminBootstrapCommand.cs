using InventoryManagementSystem.Infrastructure.Data;

namespace InventoryManagementSystem.Web.Configuration;

/// <summary>Runs the explicit one-shot administrator bootstrap command.</summary>
public static class AdminBootstrapCommand
{
    public const string Switch = "--bootstrap-admin";

    public static bool IsRequested(IEnumerable<string> args)
    {
        return args.Any(argument => string.Equals(argument, Switch, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration configuration)
    {
        var bootstrapConfiguration = configuration.GetSection("BootstrapAdmin");
        var tenantId = Require(bootstrapConfiguration["TenantId"], "BootstrapAdmin:TenantId");
        var email = Require(bootstrapConfiguration["Email"], "BootstrapAdmin:Email");
        var password = Require(bootstrapConfiguration["Password"], "BootstrapAdmin:Password");

        using var scope = services.CreateScope();
        var bootstrapper = scope.ServiceProvider.GetRequiredService<AdminBootstrapService>();
        await bootstrapper.BootstrapAsync(tenantId, email, password);

        Console.WriteLine($"Administrator bootstrap completed for tenant '{tenantId}'.");
        return 0;
    }

    private static string Require(string? value, string configurationKey)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{configurationKey} must be configured for {Switch}.");
        }

        return value;
    }
}
