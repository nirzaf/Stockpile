using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Merconiq.Core.Options;
using Merconiq.Web.Tenancy;

namespace Merconiq.Web.BackgroundServices;

public class ForecastBackgroundService : BackgroundService
{
    private readonly ITenantForecastRunner _tenantForecastRunner;
    private readonly IOptions<TenantOptions> _tenantOptions;
    private readonly IOptions<ForecastingOptions> _forecastingOptions;
    private readonly ILogger<ForecastBackgroundService> _logger;

    public ForecastBackgroundService(
        ITenantForecastRunner tenantForecastRunner,
        IOptions<TenantOptions> tenantOptions,
        IOptions<ForecastingOptions> forecastingOptions,
        ILogger<ForecastBackgroundService> logger)
    {
        _tenantForecastRunner = tenantForecastRunner;
        _tenantOptions = tenantOptions;
        _forecastingOptions = forecastingOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_forecastingOptions.Value.Enabled)
        {
            _logger.LogInformation("Forecast background service is disabled by Forecasting:Enabled.");
            return;
        }

        _logger.LogInformation("Forecast Background Service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Starting background ML model pre-training...");

                var tenantIds = _tenantOptions.Value.GetTenantIds();
                foreach (var tenantId in tenantIds)
                {
                    await _tenantForecastRunner.RunAsync(tenantId, stoppingToken);
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
