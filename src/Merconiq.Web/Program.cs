using Merconiq.Web.Configuration;

namespace Merconiq.Web;

/// <summary>Application composition root.</summary>
public class Program
{
    /// <summary>Builds, configures, and runs the ASP.NET Core web application.</summary>
    /// <param name="args">Command-line arguments passed to the host.</param>
    public static async Task<int> Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Host.ConfigureInventoryLogging();
        builder.Services
            .AddInventoryObservability(builder.Configuration)
            .AddInventoryPersistence(builder.Configuration, builder.Environment)
            .AddInventoryIdentityAndAuthentication(builder.Configuration, builder.Environment)
            .AddInventoryApplicationServices(builder.Configuration)
            .AddInventoryPresentation(builder.Configuration);

        var app = builder.Build();

        if (AdminBootstrapCommand.IsRequested(args))
        {
            return await AdminBootstrapCommand.RunAsync(app.Services, builder.Configuration);
        }

        app.UseInventoryPipeline(builder.Configuration)
            .MapInventoryEndpoints();

        await app.InitializeInventoryApplicationAsync();
        await app.RunAsync();
        return 0;
    }
}
