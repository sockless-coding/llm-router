using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Background service that periodically purges old request logs based on the retention policy.
/// Runs every hour (configurable via the cleanup interval setting).
/// </summary>
public class RetentionCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RetentionCleanupService> _log;
    private readonly GatewaySettings _settings;

    public RetentionCleanupService(
        IServiceProvider serviceProvider,
        ILogger<RetentionCleanupService> log,
        IOptions<GatewaySettings> settings)
    {
        _serviceProvider = serviceProvider;
        _log = log;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("RetentionCleanupService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var retentionDays = _settings.RequestLogRetentionDays;

                if (retentionDays <= 0)
                {
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                    continue;
                }

                // Resolve scoped services within a scope in the background service
                using var scope = _serviceProvider.CreateScope();
                var logger = scope.ServiceProvider.GetRequiredService<IApiRequestLogger>();

                var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
                var deleted = await logger.DeleteOlderThanAsync(cutoff);

                if (deleted > 0)
                {
                    _log.LogInformation("Retention cleanup: deleted {Deleted} logs older than {Cutoff}", deleted, cutoff);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Error during retention cleanup");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
