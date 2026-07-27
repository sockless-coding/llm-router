using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;

namespace LR.Application.Pages.Api.Stats;

public class SummaryModel : PageModel
{
    private readonly IStatisticsService _stats;

    public SummaryModel(IStatisticsService stats)
    {
        _stats = stats;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? ServerId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var fromStr = From ?? DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        var from = DateTimeOffset.Parse(fromStr);
        var to = DateTimeOffset.UtcNow;

        long totalRequests, totalTokens;
        double avgLatency;

        if (ServerId.HasValue)
        {
            totalRequests = await _stats.GetTotalRequestCountAsync(ServerId.Value, from, to);
            totalTokens = await _stats.GetTotalTokensProcessedAsync(ServerId.Value, from, to);
            avgLatency = await _stats.GetAvgTotalLatencyAsync(ServerId.Value, from, to);
        }
        else
        {
            totalRequests = await _stats.GetTotalRequestCountAsync(from: from, to: to);
            totalTokens = await _stats.GetTotalTokensProcessedAsync(from: from, to: to);
            avgLatency = await _stats.GetAvgTotalLatencyAsync(from: from, to: to);
        }

        var avgPromptTps = await _stats.GetAvgPromptTokensPerSecByServerAsync(from, to);
        var avgGenTps = await _stats.GetAvgGenTokensPerSecByServerAsync(from, to);

        return new JsonResult(new
        {
            from = from.ToString("O"),
            to = to.ToString("O"),
            totalRequests,
            totalTokens,
            avgLatencyMs = Math.Round(avgLatency, 2),
            serverAvgPromptTps = avgPromptTps.ToDictionary(k => k.Key.ToString(), v => Math.Round(v.Value, 2)),
            serverAvgGenTps = avgGenTps.ToDictionary(k => k.Key.ToString(), v => Math.Round(v.Value, 2))
        });
    }
}
