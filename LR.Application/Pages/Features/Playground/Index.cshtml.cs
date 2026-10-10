using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.Playground;

public class IndexModel : PageModel
{
    private readonly IPresetManager _presetManager;

    public IndexModel(IPresetManager presetManager)
    {
        _presetManager = presetManager;
    }

    public IReadOnlyList<ModelPreset> Presets { get; set; } = new List<ModelPreset>();

    /// <summary>Preset to preselect, e.g. when arriving from a preset's page.</summary>
    [BindProperty(SupportsGet = true)]
    public Guid? PresetId { get; set; }

    public void OnGet()
    {
        Presets = _presetManager.GetAllPresets()
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
