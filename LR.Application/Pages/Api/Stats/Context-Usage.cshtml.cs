using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;

namespace LR.Application.Pages.Api.Stats;

public class ContextUsageModel : PageModel
{
    private readonly IStatisticsService _stats;

    public ContextUsageModel(IStatisticsService stats)
    {
        _stats = stats;
    }

    [BindProperty(SupportsGet = true)]
    public Guid PresetId { get; set; } = Guid.Empty;

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (PresetId == Guid.Empty)
            return new BadRequestResult();

        var from = DateTimeOffset.Parse(From ?? DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        var to = DateTimeOffset.UtcNow;

        var usageList = await _stats.GetContextUsageOverTimeAsync(PresetId, from, to);
        var result = usageList.Select(t => new { timestamp = t.Timestamp.ToString("O"), tokensUsed = t.TokensUsed }).ToList();

        return new JsonResult(result);
    }
}
