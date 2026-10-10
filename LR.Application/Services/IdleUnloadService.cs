using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Services;

/// <summary>
/// Stops running servers that have had no requests for their
/// <see cref="ServerInstance.IdleUnloadMinutes"/>, freeing device memory. The next request for one
/// of their presets starts them again through the normal routing path. Idleness is measured from
/// <see cref="IServerConcurrencyLimiter.LastActivity"/>; a server with no recorded activity (e.g.
/// one reattached after a router restart) starts its idle clock on first sight. Servers whose
/// active preset is <see cref="ModelPreset.KeepLoaded"/> are left alone.
/// </summary>
public class IdleUnloadService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServerConcurrencyLimiter _limiter;
    private readonly ILogger<IdleUnloadService> _logger;

    public IdleUnloadService(IServiceScopeFactory scopeFactory, IServerConcurrencyLimiter limiter, ILogger<IdleUnloadService> logger)
    {
        _scopeFactory = scopeFactory;
        _limiter = limiter;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await UnloadIdleServersAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Idle unload check failed; continuing.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { }
        }
    }

    private async Task UnloadIdleServersAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var serverManager = scope.ServiceProvider.GetRequiredService<IServerManager>();
        var logService = scope.ServiceProvider.GetRequiredService<IServerLogService>();
        var presetManager = scope.ServiceProvider.GetRequiredService<IPresetManager>();

        foreach (var server in await serverManager.GetAllInstancesAsync())
        {
            if (server.IdleUnloadMinutes is not > 0 || server.Status != ServerStatus.Running || _limiter.InFlight(server.Id) > 0)
                continue;

            if (server.ActivePresetId is Guid presetId && presetManager.GetById(presetId) is { KeepLoaded: true })
                continue;

            if (_limiter.LastActivity(server.Id) is not { } lastActivity)
            {
                _limiter.Touch(server.Id);
                continue;
            }

            var idle = DateTimeOffset.UtcNow - lastActivity;
            if (idle < TimeSpan.FromMinutes(server.IdleUnloadMinutes.Value))
                continue;

            _logger.LogInformation("Unloading server {Server} after {Minutes:F0} idle minutes", server.Name, idle.TotalMinutes);
            await serverManager.StopAsync(server.Id, cancellationToken);
            await logService.LogAsync(server, ServerLogLevel.Info,
                $"Unloaded after {idle.TotalMinutes:F0} minutes without requests (idle unload: {server.IdleUnloadMinutes} min).");
        }
    }
}
