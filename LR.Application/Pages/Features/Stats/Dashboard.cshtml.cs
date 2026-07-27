using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;

namespace LR.Application.Pages.Features.Stats;

public class DashboardModel : PageModel
{
    private readonly IStatisticsService _stats;
    private readonly IServerManager _serverManager;

    public DashboardModel(IStatisticsService stats, IServerManager serverManager)
    {
        _stats = stats;
        _serverManager = serverManager;
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

        foreach (var s in Servers)
            ServerNames[s.Id] = s.Name;

        // Last 24 hours summary
        TotalRequests = await _stats.GetTotalRequestCountAsync(from: now.AddDays(-1));
        AvgLatencyMs = await _stats.GetAvgTotalLatencyAsync(from: now.AddDays(-1));
        TotalTokens = await _stats.GetTotalTokensProcessedAsync(from: now.AddDays(-1));
    }
}
