using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;

namespace LR.Application.Pages.Features.Stats;

public class DashboardModel : PageModel
{
    private readonly IStatisticsService _stats;
    private readonly IServerManager _serverManager;
    private readonly LRDbContext _context;

    public DashboardModel(IStatisticsService stats, IServerManager serverManager, LRDbContext context)
    {
        _stats = stats;
        _serverManager = serverManager;
        _context = context;
    }

    public IReadOnlyList<Core.Models.ServerInstance> Servers { get; set; } = new List<Core.Models.ServerInstance>();

    // Summary data for the page header cards
    public long TotalRequests { get; set; }
    public double AvgLatencyMs { get; set; }
    public long TotalTokens { get; set; }
    public Dictionary<Guid, string> ServerNames { get; set; } = new();

    // Preset data for context usage chart
    public IReadOnlyList<Core.Models.ModelPreset> Presets { get; set; } = new List<Core.Models.ModelPreset>();

    public async Task OnGetAsync()
    {
        var now = DateTimeOffset.UtcNow;
        Servers = _serverManager.GetAllInstances();
        Presets = await _stats.GetPresetsForContextUsageAsync(now.AddDays(-1), now);

        // Build ServerNames from both active servers AND historical server names in stats data
        foreach (var s in Servers)
            ServerNames[s.Id] = s.Name;

        // Also include any server IDs that appear in statistics but aren't currently active
        var statServerIds = await _context.ModelStatistics
            .Where(s => s.Timestamp >= now.AddDays(-1))
            .Select(s => new { s.ServerInstanceId, s.ServerInstance!.Name })
            .Distinct()
            .ToListAsync();

        foreach (var entry in statServerIds)
            ServerNames[entry.ServerInstanceId] = entry.Name;

        // Last 24 hours summary
        TotalRequests = await _stats.GetTotalRequestCountAsync(from: now.AddDays(-1));
        AvgLatencyMs = await _stats.GetAvgTotalLatencyAsync(from: now.AddDays(-1));
        TotalTokens = await _stats.GetTotalTokensProcessedAsync(from: now.AddDays(-1));
    }
}
