using System;
using System.Threading;
using System.Threading.Tasks;
using InventoryManagementSystem.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;

namespace InventoryManagementSystem.Web.BackgroundServices;

public class ForecastBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ForecastBackgroundService> _logger;
    private readonly IMemoryCache _cache;

    public ForecastBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<ForecastBackgroundService> logger,
        IMemoryCache cache)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _cache = cache;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Forecast Background Service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Starting background ML model pre-training...");

                using (var scope = _serviceProvider.CreateScope())
                {
                    var forecastService = scope.ServiceProvider.GetRequiredService<IDemandForecastService>();
                    var itemsForecast = await forecastService.ForecastAllItemsAsync(30);

                    // Cache the results
                    _cache.Set("forecast_all", itemsForecast, TimeSpan.FromHours(4));

                    foreach (var forecast in itemsForecast)
                    {
                        _cache.Set($"forecast_{forecast.ItemId}", forecast, TimeSpan.FromHours(4));
                    }
                }

                _logger.LogInformation("Background ML model pre-training completed successfully. Cached forecasts.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during background ML model pre-training.");
            }

            // Wait 2 hours before next training cycle
            await Task.Delay(TimeSpan.FromHours(2), stoppingToken);
        }
    }
}
