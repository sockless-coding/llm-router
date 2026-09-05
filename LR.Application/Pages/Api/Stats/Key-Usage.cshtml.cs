using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;

namespace LR.Application.Pages.Api.Stats;

public class KeyUsageModel : PageModel
{
    private readonly IStatisticsService _stats;

    public KeyUsageModel(IStatisticsService stats)
    {
        _stats = stats;
    }

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var from = DateTimeOffset.Parse(From ?? DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        var to = DateTimeOffset.UtcNow;

        var usage = await _stats.GetUsageByApiKeyAsync(from, to);
        var rows = usage.Select(u => new
        {
            apiKeyId = u.ApiKeyId,
            name = u.Name,
            keyPrefix = u.KeyPrefix,
            requestCount = u.RequestCount,
            inputTokens = u.PromptTokens,
            outputTokens = u.GeneratedTokens,
            totalTokens = u.TotalTokens,
            avgLatencyMs = Math.Round(u.AvgLatencyMs, 1)
        }).ToList();

        return new JsonResult(rows);
    }
}
