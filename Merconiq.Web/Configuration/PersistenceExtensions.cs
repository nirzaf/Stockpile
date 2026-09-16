using Merconiq.Infrastructure.Data;
using Merconiq.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

namespace Merconiq.Web.Configuration;

public static class PersistenceExtensions
{
    public static IServiceCollection AddInventoryPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<KeyStorageOptions>()
            .Bind(configuration.GetSection(KeyStorageOptions.SectionName));

        var dataProtection = services.AddDataProtection()
            .SetApplicationName("Merconiq");
        if (environment.IsProduction())
        {
            var keyPath = configuration
                .GetSection(KeyStorageOptions.SectionName)
                .GetValue<string>(nameof(KeyStorageOptions.KeyStoragePath))
                ?? "/app/data/keys";
            Directory.CreateDirectory(keyPath);
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        }

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IConfiguration>((options, currentConfiguration) =>
                ForwardedHeadersConfiguration.Configure(options, currentConfiguration));

        if (!environment.IsEnvironment("Testing"))
        {
            services.AddDbContext<InventoryDbContext>(options =>
                options.UseNpgsql(
                    configuration.GetConnectionString("DefaultConnection"),
                    npgsql => npgsql.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(30),
                        errorCodesToAdd: null)));
        }

        services.AddDatabaseDeveloperPageExceptionFilter();
        return services;
    }
}
