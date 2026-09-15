using InventoryManagementSystem.Core.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace InventoryManagementSystem.Web.Configuration;

public static class ObservabilityExtensions
{
    public static void ConfigureInventoryLogging(this IHostBuilder host)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("logs/inventory-.txt", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        host.UseSerilog();
    }

    public static IServiceCollection AddInventoryObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("OpenTelemetry:Enabled"))
        {
            return services;
        }

        var openTelemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("Stockpile"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddEntityFrameworkCoreInstrumentation())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(InventoryTelemetry.MeterName));

        var telemetryEndpoint = configuration["OpenTelemetry:OtlpEndpoint"];
        if (!string.IsNullOrWhiteSpace(telemetryEndpoint))
        {
            openTelemetry.UseOtlpExporter(OtlpExportProtocol.HttpProtobuf, new Uri(telemetryEndpoint));
        }

        return services;
    }
}
