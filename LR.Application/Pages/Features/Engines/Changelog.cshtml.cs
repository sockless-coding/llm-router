using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.Engines;

public class EngineChangelogModel : PageModel
{
    private readonly IEngineBuildManager _manager;

    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public LlamaCppBuild? Build { get; set; }
    public EngineBuildUpdateStatus? Status { get; set; }

    public EngineChangelogModel(IEngineBuildManager manager) => _manager = manager;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Build = await _manager.GetBuildAsync(Id);
        if (Build is null) return NotFound();

        Status = await _manager.GetUpdateStatusAsync(Id, ct);
        return Page();
    }
}
