using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Merconiq.Infrastructure.Data;
using Merconiq.Web.Middleware;
using Merconiq.Web.Security;
using Merconiq.Web.Tenancy;
using Serilog;

namespace Merconiq.Web.Configuration;

public static class PipelineExtensions
{
    private static readonly string[] SupportedCultures = ["en-US", "ar-SA"];

    public static WebApplication UseInventoryPipeline(
        this WebApplication app,
        IConfiguration configuration)
    {
        app.UseForwardedHeaders();

        var localizationOptions = new RequestLocalizationOptions()
            .SetDefaultCulture(SupportedCultures[0])
            .AddSupportedCultures(SupportedCultures)
            .AddSupportedUICultures(SupportedCultures);
        app.UseRequestLocalization(localizationOptions);
        app.UseResponseCompression();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(options =>
                options.SwaggerEndpoint("/swagger/v1/swagger.json", "Inventory API v1"));
        }

        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
        {
            app.UseExceptionHandler("/Home/Error");
            if (string.IsNullOrEmpty(configuration["DISABLE_HTTPS"]))
            {
                app.UseHsts();
            }
        }
        else
        {
            app.UseExceptionHandler(new ExceptionHandlerOptions
            {
                AllowStatusCode404Response = true
            });
        }

        if (string.IsNullOrEmpty(configuration["DISABLE_HTTPS"]))
        {
            app.UseHttpsRedirection();
        }

        app.UseStaticFiles();
        app.UseSecurityHeaders();
        app.UseSerilogRequestLogging();
        app.UseTenantContext();
        app.UseRouting();
        app.UseCors("Default");
        app.UseAuthentication();
        // API rate-limit partitions use authenticated client claims when available.
        app.UseRateLimiter();
        app.UseAuthorization();
        app.UseAntiforgery();

        return app;
    }

    public static async Task InitializeInventoryApplicationAsync(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            await db.Database.MigrateAsync();
        }
    }
}
