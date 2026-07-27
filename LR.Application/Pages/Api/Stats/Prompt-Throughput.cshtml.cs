using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;

namespace LR.Application.Pages.Api.Stats;

public class PromptThroughputModel : PageModel
{
    private readonly IStatisticsService _stats;
    private readonly IServerManager _serverManager;

    public PromptThroughputModel(IStatisticsService stats, IServerManager serverManager)
    {
        _stats = stats;
        _serverManager = serverManager;
    }

    [BindProperty(SupportsGet = true)]
    public string? From { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var from = DateTimeOffset.Parse(From ?? DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        var to = DateTimeOffset.UtcNow;

        var servers = _serverManager.GetAllInstances();
        var result = new Dictionary<string, List<object>>();

        foreach (var server in servers)
        {
            var statsList = await _stats.GetByServerAsync(server.Id, from, to);
            var points = statsList.Where(s => s.PromptProcessingMs > 0).Select(s => new
            {
                timestamp = s.Timestamp.ToString("O"),
                value = (double)s.PromptTokensProcessed / s.PromptProcessingMs * 1000
            }).ToList();
            result[server.Id.ToString()] = points.Cast<object>().ToList();
        }

        return new JsonResult(result);
    }
}
