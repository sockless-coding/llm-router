using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;

namespace LR.Application.Pages.Api.Stats;

public class PromptThroughputModel : PageModel
{
    private readonly IStatisticsService _stats;
    private readonly LRDbContext _context;

    public PromptThroughputModel(IStatisticsService stats, LRDbContext context)
    {
        _stats = stats;
        _context = context;
    }

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var from = DateTimeOffset.Parse(From ?? DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        var to = DateTimeOffset.UtcNow;

        // Get server IDs that have stats data in this time range (works even if servers are offline)
        var serverIds = await _context.ModelStatistics
            .Where(s => s.Timestamp >= from && s.Timestamp <= to)
            .Select(s => s.ServerInstanceId)
            .Distinct()
            .ToListAsync();

        var result = new Dictionary<string, List<object>>();

        foreach (var serverId in serverIds)
        {
            var statsList = await _stats.GetByServerAsync(serverId, from, to);
            var points = statsList.Where(s => s.PromptProcessingMs > 0).Select(s => new
            {
                timestamp = s.Timestamp.ToString("O"),
                value = (double)s.PromptTokensProcessed / s.PromptProcessingMs * 1000
            }).ToList();
            result[serverId.ToString()] = points.Cast<object>().ToList();
        }

        return new JsonResult(result);
    }
}
