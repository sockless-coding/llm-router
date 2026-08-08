using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.RequestLog;

public class IndexModel : PageModel
{
    private readonly IApiRequestLogger _requestLogger;

    public IReadOnlyList<ApiRequestLog> Logs { get; set; } = new List<ApiRequestLog>();
    public long TotalCount { get; set; }
    public ApiProtocol? SelectedProtocol { get; set; }
    public string TimeRange { get; set; } = "24h";

    // Summary stats for top cards
    public long TotalToday { get; set; }
    public long OpenAIToday { get; set; }
    public long ClaudeToday { get; set; }
    public long OllamaToday { get; set; }
    public double AvgLatencyMs { get; set; }

    public IndexModel(IApiRequestLogger requestLogger)
    {
        _requestLogger = requestLogger;
    }

    public async Task OnGetAsync([FromQuery] ApiProtocol? protocol, [FromQuery] string timeRange = "24h")
    {
        SelectedProtocol = protocol;
        TimeRange = timeRange;

        DateTimeOffset fromTime = TimeRange switch
        {
            "1h" => DateTimeOffset.UtcNow.AddHours(-1),
            "6h" => DateTimeOffset.UtcNow.AddHours(-6),
            "7d" => DateTimeOffset.UtcNow.AddDays(-7),
            _ => DateTimeOffset.UtcNow.AddDays(-1)
        };

        var (logs, totalCount) = await _requestLogger.GetRecentLogsAsync(50, protocol, fromTime);
        Logs = logs;
        TotalCount = totalCount;

        var stats = await _requestLogger.GetSummaryStatsAsync();
        TotalToday = stats.TotalToday;
        OpenAIToday = stats.OpenAIToday;
        ClaudeToday = stats.ClaudeToday;
        OllamaToday = stats.OllamaToday;
        AvgLatencyMs = stats.AvgLatencyMs;
    }
}
