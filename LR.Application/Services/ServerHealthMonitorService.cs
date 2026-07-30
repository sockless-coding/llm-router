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

                var instances = await serverManager.GetAllInstancesAsync();

                foreach (var instance in instances.Where(i => i.Status == Core.Models.ServerStatus.Running))
                {
                    try
                    {
                        // Call the real provider health check
                        var provider = serverManager.GetProvider(instance.Id);
                        bool isHealthy;
                        if (provider is not null)
                        {
                            isHealthy = await provider.HealthCheckAsync(stoppingToken);
                        }
                        else
                        {
                            _logger.LogWarning("No provider registered for instance {Id} ({Name}), marking unhealthy.",
                                instance.Id, instance.Name);
                            isHealthy = false;
                        }

                        // Persist the health status via ServerManager (uses DbContext)
                        await serverManager.UpdateHealthAsync(instance.Id, isHealthy);

                        _logger.LogDebug("Health check: {Name} - Status={Status}, Healthy={Healthy}",
                            instance.Name, instance.Status, isHealthy);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Health check failed for instance {Id} ({Name}), marking unhealthy.",
                            instance.Id, instance.Name);
                        await serverManager.UpdateHealthAsync(instance.Id, false);
                    }
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
