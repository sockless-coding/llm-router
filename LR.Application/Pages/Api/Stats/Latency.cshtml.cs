using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;

namespace LR.Application.Pages.Api.Stats;

public class LatencyModel : PageModel
{
    private readonly IStatisticsService _stats;
    private readonly IServerManager _serverManager;

    public LatencyModel(IStatisticsService stats, IServerManager serverManager)
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
            var points = statsList.Select(s => new
            {
                timestamp = s.Timestamp.ToString("O"),
                value = s.TotalLatencyMs
            }).ToList();
            result[server.Id.ToString()] = points.Cast<object>().ToList();
        }

        return new JsonResult(result);
    }
}
