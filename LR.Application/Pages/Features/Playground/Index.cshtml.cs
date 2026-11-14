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

    /// <summary>Decision presets, offered in the picker as a switch to the Decide page.</summary>
    public IReadOnlyList<ModelPreset> DecisionPresets { get; set; } = new List<ModelPreset>();

    /// <summary>Preset to preselect, e.g. when arriving from a preset's page.</summary>
    [BindProperty(SupportsGet = true)]
    public Guid? PresetId { get; set; }

    /// <summary>Presets whose model is running right now; the picker defaults to one of these so a send doesn't swap models unnoticed.</summary>
    public IReadOnlySet<Guid> LoadedPresetIds { get; set; } = new HashSet<Guid>();

    public IActionResult OnGet()
    {
        // This page chats; image and video presets have their own Generate page.
        var servers = _serverManager.GetAllInstances();
        LoadedPresetIds = servers.Where(s => s.Status == ServerStatus.Running && s.ActivePresetId is not null).Select(s => s.ActivePresetId!.Value).ToHashSet();
        var all = _presetManager.GetAllPresets().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Presets = all.ForChat(servers, _engines).ToList();
        ImagePresets = all.OfModality(servers, _engines, EngineModality.Image).ToList();

        DecisionPresets = all.ForDecisions(servers, _engines).ToList();

        if (PresetId is Guid id && ImagePresets.Any(p => p.Id == id))
            return RedirectToPage("Generate", new { presetId = id });
        if (PresetId is Guid did && Presets.All(p => p.Id != did) && DecisionPresets.Any(p => p.Id == did))
            return RedirectToPage("Decide", new { presetId = did });
        if (Presets.Count == 0 && ImagePresets.Count > 0)
            return RedirectToPage("Generate");
        if (Presets.Count == 0 && DecisionPresets.Count > 0)
            return RedirectToPage("Decide");
        return Page();
    }
}
