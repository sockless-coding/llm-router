using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Application.Pages.Api;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.Playground;

public class IndexModel : PageModel
{
    private readonly IPresetManager _presetManager;
    private readonly IServerManager _serverManager;
    private readonly IEngineCatalog _engines;

    public IndexModel(IPresetManager presetManager, IServerManager serverManager, IEngineCatalog engines)
    {
        _presetManager = presetManager;
        _serverManager = serverManager;
        _engines = engines;
    }

    public IReadOnlyList<ModelPreset> Presets { get; set; } = new List<ModelPreset>();

    /// <summary>Preset to preselect, e.g. when arriving from a preset's page.</summary>
    [BindProperty(SupportsGet = true)]
    public Guid? PresetId { get; set; }

    public void OnGet()
    {
        // The Playground chats — image presets aren't offered.
        Presets = _presetManager.GetAllPresets()
            .OfModality(_serverManager.GetAllInstances(), _engines, EngineModality.Text)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
