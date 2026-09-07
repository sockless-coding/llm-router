using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Application.Pages;

public class DashboardModel : PageModel
{
    private readonly IServerManager _serverManager;
    private readonly IStatisticsService _stats;
    private readonly IApiRequestLogger _requestLogger;
    private readonly LRDbContext _context;
    private readonly IServerConcurrencyLimiter _concurrencyLimiter;
    private readonly GatewaySettings _settings;

    public IReadOnlyList<ServerInstance> Servers { get; set; } = new List<ServerInstance>();

    /// <summary>
    /// Name of each server's active preset, keyed by preset ID — <see cref="IServerManager.GetAllInstances"/>
    /// doesn't populate the ActivePreset navigation, so this is fetched separately for display.
    /// </summary>
    public Dictionary<Guid, string> ActivePresetNames { get; set; } = new();

    /// <summary>
    /// Initial live slot usage per server id: (in-flight requests, parallel-slot capacity).
    /// Only llama.cpp servers that are running get an entry; the UI then keeps these current
    /// over SignalR (<c>ReceiveServerLoad</c>).
    /// </summary>
    public Dictionary<Guid, (int InFlight, int MaxSlots)> ServerSlots { get; set; } = new();

    // 24h activity summary for the stat tiles
    public long TotalRequests24h { get; set; }
    public long TotalTokens24h { get; set; }
    public double AvgLatencyMs24h { get; set; }

    public IReadOnlyList<ApiRequestLog> RecentRequests { get; set; } = new List<ApiRequestLog>();

    public DashboardModel(
        IServerManager serverManager,
        IStatisticsService stats,
        IApiRequestLogger requestLogger,
        LRDbContext context,
        IServerConcurrencyLimiter concurrencyLimiter,
        GatewaySettings settings)
    {
        _serverManager = serverManager;
        _stats = stats;
        _requestLogger = requestLogger;
        _context = context;
        _concurrencyLimiter = concurrencyLimiter;
        _settings = settings;
    }

    public async Task OnGetAsync()
    {
        Servers = _serverManager.GetAllInstances();

        var activePresetIds = Servers
            .Where(s => s.ActivePresetId.HasValue)
            .Select(s => s.ActivePresetId!.Value)
            .Distinct()
            .ToList();
        Dictionary<Guid, ModelPreset> activePresets = new();
        if (activePresetIds.Count > 0)
        {
            activePresets = await _context.ModelPresets
                .Where(p => activePresetIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p);
            ActivePresetNames = activePresets.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        }

        foreach (var s in Servers.Where(s => s.Engine == ServerEngine.LlamaCpp && s.Status == ServerStatus.Running))
        {
            var preset = s.ActivePresetId is Guid pid && activePresets.TryGetValue(pid, out var p) ? p : null;
            var maxSlots = LlamaSlotCapacity.Resolve(_serverManager.GetProvider(s.Id), preset, _settings.DefaultParallelSlots);
            ServerSlots[s.Id] = (_concurrencyLimiter.InFlight(s.Id), maxSlots);
        }

        var from = DateTimeOffset.UtcNow.AddDays(-1);
        TotalRequests24h = await _stats.GetTotalRequestCountAsync(from: from);
        TotalTokens24h = await _stats.GetTotalTokensProcessedAsync(from: from);
        AvgLatencyMs24h = await _stats.GetAvgTotalLatencyAsync(from: from);

        var (logs, _) = await _requestLogger.GetRecentLogsAsync(6, from: from);
        RecentRequests = logs;
    }
}
