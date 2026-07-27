using LR.Core.Interfaces;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Presets;

public class PresetsListModel : PageModel
{
    private readonly IPresetManager _presetManager;
    private readonly IServerManager _serverManager;

    public IReadOnlyList<Core.Models.ModelPreset> Presets { get; set; } = new List<Core.Models.ModelPreset>();
    public IReadOnlyList<Core.Models.ServerInstance> Servers { get; set; } = new List<Core.Models.ServerInstance>();

    public PresetsListModel(IPresetManager presetManager, IServerManager serverManager)
    {
        _presetManager = presetManager;
        _serverManager = serverManager;
    }

    public void OnGet()
    {
        Presets = _presetManager.GetByServerInstanceId(Guid.Empty); // All presets for now (we'd filter by query param)
        Servers = _serverManager.GetAllInstances();
    }
}

