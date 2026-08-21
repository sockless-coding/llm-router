using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages;

public class DashboardModel : PageModel
{
    private readonly IServerManager _serverManager;
    private readonly IStatisticsService _stats;
    private readonly IApiRequestLogger _requestLogger;
    private readonly LRDbContext _context;

    public IReadOnlyList<ServerInstance> Servers { get; set; } = new List<ServerInstance>();

    /// <summary>
    /// Name of each server's active preset, keyed by preset ID — <see cref="IServerManager.GetAllInstances"/>
    /// doesn't populate the ActivePreset navigation, so this is fetched separately for display.
    /// </summary>
    public Dictionary<Guid, string> ActivePresetNames { get; set; } = new();

    // 24h activity summary for the stat tiles
    public long TotalRequests24h { get; set; }
    public long TotalTokens24h { get; set; }
    public double AvgLatencyMs24h { get; set; }

    public IReadOnlyList<ApiRequestLog> RecentRequests { get; set; } = new List<ApiRequestLog>();

    public DashboardModel(IServerManager serverManager, IStatisticsService stats, IApiRequestLogger requestLogger, LRDbContext context)
    {
        _serverManager = serverManager;
        _stats = stats;
        _requestLogger = requestLogger;
        _context = context;
    }

    public async Task OnGetAsync()
    {
        Servers = _serverManager.GetAllInstances();

        var activePresetIds = Servers
            .Where(s => s.ActivePresetId.HasValue)
            .Select(s => s.ActivePresetId!.Value)
            .Distinct()
            .ToList();
        if (activePresetIds.Count > 0)
        {
            ActivePresetNames = await _context.ModelPresets
                .Where(p => activePresetIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name);
        }

        var from = DateTimeOffset.UtcNow.AddDays(-1);
        TotalRequests24h = await _stats.GetTotalRequestCountAsync(from: from);
        TotalTokens24h = await _stats.GetTotalTokensProcessedAsync(from: from);
        AvgLatencyMs24h = await _stats.GetAvgTotalLatencyAsync(from: from);

        var (logs, _) = await _requestLogger.GetRecentLogsAsync(6, from: from);
        RecentRequests = logs;
    }
}
