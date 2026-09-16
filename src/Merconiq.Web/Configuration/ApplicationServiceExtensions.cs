using FluentValidation;
using Merconiq.Core.Behaviors;
using Merconiq.Core.Features.Items.Queries;
using Merconiq.Core.Interfaces;
using Merconiq.Core.Options;
using Merconiq.Core.Services;
using Merconiq.Core.Validators;
using Merconiq.Infrastructure.Data;
using Merconiq.Infrastructure.Repositories;
using Merconiq.Web.BackgroundServices;
using Merconiq.Web.Services;
using MediatR;

namespace Merconiq.Web.Configuration;

public static class ApplicationServiceExtensions
{
    public static IServiceCollection AddInventoryApplicationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddMemoryCache();
        services.AddHttpContextAccessor();

        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.AddScoped<IItemRepository, ItemRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddScoped<IItemService, ItemService>();
        services.AddScoped<ISupplierService, SupplierService>();
        services.AddScoped<ILocationService, LocationService>();
        services.AddScoped<IStockService, StockService>();
        services.AddScoped<IPurchaseOrderService, PurchaseOrderService>();
        services.AddScoped<IOrganizationService, Merconiq.Infrastructure.Services.OrganizationService>();
        services.AddScoped<IMasterDataImportService, Merconiq.Infrastructure.Services.MasterDataImportService>();

        services.AddOptions<ForecastingOptions>()
            .Bind(configuration.GetSection(ForecastingOptions.SectionName))
            .Validate(options => ForecastingImplementations.IsSupported(options.Implementation),
                $"{ForecastingOptions.SectionName}:Implementation must be '{ForecastingImplementations.ManagedMovingAverage}' or '{ForecastingImplementations.Ssa}'.")
            .ValidateOnStart();
        services.AddScoped<IDemandForecastService, DemandForecastService>();
        services.AddScoped<IAnomalyDetectionService, AnomalyDetectionService>();

        services.AddHttpClient("Webhooks", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        services.AddScoped<IWebhookDispatcher, Merconiq.Infrastructure.Services.WebhookDispatcher>();
        services.AddScoped<IIdempotencyKeyStore, IdempotencyKeyStore>();

        services.AddValidatorsFromAssemblyContaining<ItemValidator>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(GetAllItemsQuery).Assembly));

        services.AddSingleton<ITenantForecastRunner, TenantForecastRunner>();
        services.AddHostedService<ForecastBackgroundService>();
        services.AddHostedService<Merconiq.Web.BackgroundServices.WebhookDeliveryBackgroundService>();

        return services;
    }
}
