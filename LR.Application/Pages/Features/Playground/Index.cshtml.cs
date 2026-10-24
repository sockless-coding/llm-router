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

    /// <summary>Image/video presets, offered in the picker as a switch to the Generate page.</summary>
    public IReadOnlyList<ModelPreset> ImagePresets { get; set; } = new List<ModelPreset>();

    /// <summary>Preset to preselect, e.g. when arriving from a preset's page.</summary>
    [BindProperty(SupportsGet = true)]
    public Guid? PresetId { get; set; }

    public IActionResult OnGet()
    {
        // This page chats; image and video presets have their own Generate page.
        var servers = _serverManager.GetAllInstances();
        var all = _presetManager.GetAllPresets().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Presets = all.OfModality(servers, _engines, EngineModality.Text).ToList();
        ImagePresets = all.OfModality(servers, _engines, EngineModality.Image).ToList();

        if (PresetId is Guid id && ImagePresets.Any(p => p.Id == id))
            return RedirectToPage("Generate", new { presetId = id });
        if (Presets.Count == 0 && ImagePresets.Count > 0)
            return RedirectToPage("Generate");
        return Page();
    }
}
