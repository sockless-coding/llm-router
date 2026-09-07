using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using LR.Application.Hubs;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Application.Services;

/// <summary>
/// Broadcasts a compact per-server load snapshot (in-flight requests vs. parallel-slot capacity)
/// to the UI over <see cref="ServerHub"/> once a second, so the dashboard and server-detail pages
/// can show live slot usage without polling. Sent every tick (rather than only on change) so a
/// page that connects mid-stream gets correct state within a second — the payload is a handful
/// of small rows.
/// </summary>
public class ServerLoadBroadcastService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServerConcurrencyLimiter _limiter;
    private readonly IHubContext<ServerHub> _hub;
    private readonly GatewaySettings _settings;
    private readonly ILogger<ServerLoadBroadcastService> _logger;

    public ServerLoadBroadcastService(
        IServiceScopeFactory scopeFactory,
        IServerConcurrencyLimiter limiter,
        IHubContext<ServerHub> hub,
        GatewaySettings settings,
        ILogger<ServerLoadBroadcastService> logger)
    {
        _scopeFactory = scopeFactory;
        _limiter = limiter;
        _hub = hub;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var payload = await BuildSnapshotAsync();
                if (payload.Count > 0)
                    await _hub.Clients.All.SendAsync("ReceiveServerLoad", payload, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Server load broadcast tick failed; continuing.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (TaskCanceledException) { }
        }
    }

    private async Task<List<ServerLoadDto>> BuildSnapshotAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var serverManager = scope.ServiceProvider.GetRequiredService<IServerManager>();
        var presetManager = scope.ServiceProvider.GetRequiredService<IPresetManager>();

        var instances = await serverManager.GetAllInstancesAsync();
        var inFlight = _limiter.Snapshot();

        var list = new List<ServerLoadDto>(instances.Count);
        foreach (var s in instances)
        {
            inFlight.TryGetValue(s.Id, out var busy);

            int maxSlots = 0;
            if (s.Engine == ServerEngine.LlamaCpp && s.Status == ServerStatus.Running)
            {
                var preset = s.ActivePresetId is Guid pid ? presetManager.GetById(pid) : null;
                maxSlots = LlamaSlotCapacity.Resolve(serverManager.GetProvider(s.Id), preset, _settings.DefaultParallelSlots);
            }

            list.Add(new ServerLoadDto(s.Id, s.Status.ToString(), s.IsHealthy, s.Engine.ToString(), busy, maxSlots));
        }

        return list;
    }

    private readonly record struct ServerLoadDto(Guid id, string status, bool healthy, string engine, int inFlight, int maxSlots);
}
