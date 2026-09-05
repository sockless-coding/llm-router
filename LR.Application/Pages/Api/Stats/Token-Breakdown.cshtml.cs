using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;

namespace LR.Application.Pages.Api.Stats;

public class TokenBreakdownModel : PageModel
{
    private readonly IStatisticsService _stats;

    public TokenBreakdownModel(IStatisticsService stats)
    {
        _stats = stats;
    }

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var from = DateTimeOffset.Parse(From ?? DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        var to = DateTimeOffset.UtcNow;

        var buckets = await _stats.GetTokenBreakdownOverTimeAsync(from, to);
        var points = buckets.Select(b => new
        {
            timestamp = b.BucketStart.ToString("O"),
            input = b.PromptTokens,
            output = b.GeneratedTokens
        }).ToList();

        return new JsonResult(points);
    }
}
