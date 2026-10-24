using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Application.Pages.Api;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Application.Pages.Features.Playground;

/// <summary>
/// The Playground for image and video presets: prompt, settings, live progress with previews, and a
/// gallery of this session's results. Generation runs through <see cref="PlaygroundGenerationEndpoints"/>.
/// </summary>
public class GenerateModel : PageModel
{
    private readonly IPresetManager _presetManager;
    private readonly IServerManager _serverManager;
    private readonly IEngineCatalog _engines;

    public GenerateModel(IPresetManager presetManager, IServerManager serverManager, IEngineCatalog engines)
    {
        _presetManager = presetManager;
        _serverManager = serverManager;
        _engines = engines;
    }

    public IReadOnlyList<ModelPreset> Presets { get; set; } = new List<ModelPreset>();

    /// <summary>Chat presets, offered in the picker as a switch back to the chat Playground.</summary>
    public IReadOnlyList<ModelPreset> ChatPresets { get; set; } = new List<ModelPreset>();

    /// <summary>Preset to preselect, e.g. when arriving from a preset's page.</summary>
    [BindProperty(SupportsGet = true)]
    public Guid? PresetId { get; set; }

    /// <summary>Sampler/scheduler suggestions until the loaded model reports its own lists.</summary>
    public IReadOnlyList<string> SamplingMethods => StableDiffusionPresetSettings.SuggestedSamplingMethods;
    public IReadOnlyList<string> Schedulers => StableDiffusionPresetSettings.SuggestedSchedulers;

    public IActionResult OnGet()
    {
        var servers = _serverManager.GetAllInstances();
        var all = _presetManager.GetAllPresets().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Presets = all.OfModality(servers, _engines, EngineModality.Image).ToList();
        ChatPresets = all.OfModality(servers, _engines, EngineModality.Text).ToList();

        if (PresetId is Guid id && ChatPresets.Any(p => p.Id == id))
            return RedirectToPage("Index", new { presetId = id });
        return Page();
    }
}
