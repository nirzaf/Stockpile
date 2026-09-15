using FluentValidation;
using InventoryManagementSystem.Core.Behaviors;
using InventoryManagementSystem.Core.Features.Items.Queries;
using InventoryManagementSystem.Core.Interfaces;
using InventoryManagementSystem.Core.Options;
using InventoryManagementSystem.Core.Services;
using InventoryManagementSystem.Core.Validators;
using InventoryManagementSystem.Infrastructure.Data;
using InventoryManagementSystem.Infrastructure.Repositories;
using InventoryManagementSystem.Web.BackgroundServices;
using InventoryManagementSystem.Web.Services;
using MediatR;

namespace InventoryManagementSystem.Web.Configuration;

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
        services.AddScoped<IWebhookDispatcher, InventoryManagementSystem.Infrastructure.Services.WebhookDispatcher>();
        services.AddScoped<IIdempotencyKeyStore, IdempotencyKeyStore>();

        services.AddValidatorsFromAssemblyContaining<ItemValidator>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(GetAllItemsQuery).Assembly));

        services.AddSingleton<ITenantForecastRunner, TenantForecastRunner>();
        services.AddHostedService<ForecastBackgroundService>();
        services.AddHostedService<InventoryManagementSystem.Web.BackgroundServices.WebhookDeliveryBackgroundService>();

        return services;
    }
}
