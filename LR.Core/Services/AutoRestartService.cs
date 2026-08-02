using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Service for automatically restarting crashed server instances with configurable retry limits.
/// </summary>
public class AutoRestartService : IAutoRestartService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServerManager _serverManager;
    private readonly IServerLogService _logService;
    private readonly ILogger<AutoRestartService> _logger;
    private readonly ConcurrentDictionary<Guid, bool> _restarting = new();

    public AutoRestartService(
        IServiceScopeFactory scopeFactory,
        IServerManager serverManager,
        IServerLogService logService,
        ILogger<AutoRestartService> logger)
    {
        _scopeFactory = scopeFactory;
        _serverManager = serverManager;
        _logService = logService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> AttemptRestartAsync(ServerInstance instance)
    {
        // Prevent concurrent restart attempts
        if (!_restarting.TryAdd(instance.Id, true))
        {
            _logger.LogWarning("Auto-restart already in progress for server {ServerName}", instance.Name);
            return false;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LRDbContext>();

            // Check if we've exceeded the max restart count
            if (instance.RestartCount >= instance.MaxRestarts)
            {
                await _logService.LogAsync(instance, ServerLogLevel.Error,
                    $"Max restart attempts ({instance.MaxRestarts}) reached. Server marked as Error.");

                var dbServer = await db.ServerInstances.FindAsync(instance.Id);
                if (dbServer != null)
                {
                    dbServer.Status = ServerStatus.Error;
                    dbServer.LastErrorMessage = $"Max restart attempts ({instance.MaxRestarts}) reached.";
                    dbServer.LastErrorTime = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }

                _logger.LogError("Server {ServerName} exceeded max restart count of {MaxRestarts}", instance.Name, instance.MaxRestarts);
                return false;
            }

            // Increment restart count
            var srv = await db.ServerInstances.FindAsync(instance.Id);
            if (srv == null)
            {
                _logger.LogWarning("Server {ServerName} not found in database, cannot restart.", instance.Name);
                return false;
            }

            srv.RestartCount++;
            await db.SaveChangesAsync();

            // Update the local instance too
            instance.RestartCount = srv.RestartCount;

            _logger.LogInformation("Attempting auto-restart {RestartCount}/{MaxRestarts} for server {ServerName}",
                instance.RestartCount, instance.MaxRestarts, instance.Name);

            await _logService.LogAsync(instance, ServerLogLevel.Info,
                $"Auto-restart attempt {instance.RestartCount}/{instance.MaxRestarts} initiated.");

            // Attempt to restart the server via ServerManager.StartAsync (uses active preset)
            var success = await _serverManager.StartAsync(instance.Id);

            if (!success)
            {
                await _logService.LogAsync(instance, ServerLogLevel.Error,
                    $"Auto-restart attempt {instance.RestartCount} failed. Server marked as Error.");

                srv.Status = ServerStatus.Error;
                srv.LastErrorMessage = $"Auto-restart attempt {instance.RestartCount} failed.";
                srv.LastErrorTime = DateTime.UtcNow;
                await db.SaveChangesAsync();

                _logger.LogError("Auto-restart of server {ServerName} failed on attempt {RestartCount}", instance.Name, instance.RestartCount);
            }
            else
            {
                _logger.LogInformation("Server {ServerName} restarted successfully on attempt {RestartCount}", instance.Name, instance.RestartCount);
                await _logService.LogAsync(instance, ServerLogLevel.Info,
                    $"Auto-restart successful on attempt {instance.RestartCount}.");
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during auto-restart of server {ServerName}", instance.Name);
            await _logService.LogAsync(instance, ServerLogLevel.Error,
                $"Auto-restart failed with exception: {ex.Message}");

            // Update status to Error via DbContext
            using var errScope = _scopeFactory.CreateScope();
            var db = errScope.ServiceProvider.GetRequiredService<LRDbContext>();
            var server = await db.ServerInstances.FindAsync(instance.Id);
            if (server != null)
            {
                server.Status = ServerStatus.Error;
                server.LastErrorMessage = ex.Message;
                server.LastErrorTime = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            return false;
        }
        finally
        {
            _restarting.TryRemove(instance.Id, out _);
        }
    }

    /// <inheritdoc />
    public void ResetRestartCount(int serverInstanceId)
    {
        // This will be called from ServerManager when a manual start succeeds
        // The actual DB update happens in ServerManager to avoid circular dependencies
    }
}
