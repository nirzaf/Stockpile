using System;
using System.Threading;
using System.Threading.Tasks;
using InventoryManagementSystem.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InventoryManagementSystem.Web.BackgroundServices;

public class ForecastBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ForecastBackgroundService> _logger;

    public ForecastBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<ForecastBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
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
                    await forecastService.ForecastAllItemsAsync(30);
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
