using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Application.Pages.Features.Engines;

public class EngineBuildDetailModel : PageModel
{
    private readonly IEngineBuildManager _manager;
    private readonly EngineBuildService _buildService;

    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public LlamaCppBuild? Build { get; set; }
    public int ServerUsage { get; set; }
    public string? BuildLog { get; set; }
    public bool InFlight { get; set; }

    public EngineBuildDetailModel(IEngineBuildManager manager, EngineBuildService buildService)
    {
        _manager = manager;
        _buildService = buildService;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        Build = await _manager.GetBuildAsync(Id);
        if (Build is null) return NotFound();

        ServerUsage = await _manager.GetServerUsageCountAsync(Id);
        BuildLog = await _buildService.ReadBuildLogAsync(Id);
        InFlight = Build.Status is EngineBuildStatus.Downloading or EngineBuildStatus.Building;
        return Page();
    }

    /// <summary>Polled by the Detail page while a build is in flight, to refresh the log tail.</summary>
    public async Task<IActionResult> OnGetLogAsync(Guid id)
    {
        var log = await _buildService.ReadBuildLogAsync(id) ?? "";
        var build = await _manager.GetBuildAsync(id);
        return new JsonResult(new
        {
            status = build?.Status.ToString(),
            inFlight = build?.Status is EngineBuildStatus.Downloading or EngineBuildStatus.Building,
            log,
        });
    }
}
