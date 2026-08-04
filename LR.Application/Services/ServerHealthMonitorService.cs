using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

using LR.Core.Interfaces;
using LR.Core.Models;

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

                        if (!isHealthy)
                        {
                            var errorMsg = $"Health check failed for {instance.Name}. Server may be unresponsive.";

                            // UpdateHealthAsync above loaded this instance via FindAsync, so it's already tracked.
                            // Don't call .Update() on a detached copy — just read the tracked entity back.
                            var db = scope.ServiceProvider.GetRequiredService<LR.Core.Data.LRDbContext>();
                            if (db.ChangeTracker.Entries<ServerInstance>().FirstOrDefault(e => e.Entity.Id == instance.Id)?.Entity is ServerInstance trackedInstance)
                            {
                                trackedInstance.LastErrorMessage = errorMsg;
                                trackedInstance.LastErrorTime = DateTime.UtcNow;
                                await db.SaveChangesAsync();
                            }

                            var logService = scope.ServiceProvider.GetService<IServerLogService>();
                            if (logService != null)
                            {
                                await logService.LogAsync(instance, ServerLogLevel.Warning,
                                    $"Health check failed. Server is running but not responding to health checks.");
                            }
                        }

                        _logger.LogDebug("Health check: {Name} - Status={Status}, Healthy={Healthy}",
                            instance.Name, instance.Status, isHealthy);
                    }
                    catch (Exception ex)
                    {
                        var errorMsg = $"Health check exception: {ex.Message}";
                        _logger.LogWarning(ex, "Health check failed for instance {Id} ({Name}), marking unhealthy.",
                            instance.Id, instance.Name);

                        // Update the tracked entity directly to avoid EF Core tracking conflicts
                        try
                        {
                            var db = scope.ServiceProvider.GetRequiredService<LR.Core.Data.LRDbContext>();
                            if (db.ChangeTracker.Entries<ServerInstance>().FirstOrDefault(e => e.Entity.Id == instance.Id)?.Entity is ServerInstance trackedInstance)
                            {
                                trackedInstance.LastErrorMessage = errorMsg;
                                trackedInstance.LastErrorTime = DateTime.UtcNow;
                                await db.SaveChangesAsync();
                            }
                        }
                        catch { /* Best effort — don't let logging failures break health checks */ }

                        var logService = scope.ServiceProvider.GetService<IServerLogService>();
                        if (logService != null)
                        {
                            await logService.LogAsync(instance, ServerLogLevel.Error,
                                $"Health check failed with exception: {ex.Message}");
                        }

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
