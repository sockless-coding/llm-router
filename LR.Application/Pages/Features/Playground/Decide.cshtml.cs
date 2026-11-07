using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Application.Pages.Api;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.Playground;

/// <summary>
/// The Playground for decision presets (Jev-style models: OpenJev, Laya, Kev…): a state, typed
/// questions (choice, score, noul), and the probabilities the model returns for each. Requests run
/// through <see cref="SystemOneGateway"/>, the same pipeline as <c>POST /v1/systemone</c>.
/// </summary>
public class DecideModel : PageModel
{
    private readonly IPresetManager _presetManager;
    private readonly IServerManager _serverManager;
    private readonly IEngineCatalog _engines;

    public DecideModel(IPresetManager presetManager, IServerManager serverManager, IEngineCatalog engines, GatewaySettings gateway)
    {
        _presetManager = presetManager;
        _serverManager = serverManager;
        _engines = engines;
        RoutingPort = gateway.RoutingPort > 0 ? gateway.RoutingPort : gateway.Port;
    }

    /// <summary>The port <c>/v1/*</c> listens on, for the copyable curl command.</summary>
    public int RoutingPort { get; }

    public IReadOnlyList<ModelPreset> Presets { get; set; } = new List<ModelPreset>();

    /// <summary>Chat presets, offered in the picker as a switch to the chat Playground.</summary>
    public IReadOnlyList<ModelPreset> ChatPresets { get; set; } = new List<ModelPreset>();

    /// <summary>Image/video presets, offered in the picker as a switch to the Generate page.</summary>
    public IReadOnlyList<ModelPreset> ImagePresets { get; set; } = new List<ModelPreset>();

    /// <summary>Preset to preselect, e.g. when arriving from a preset's page.</summary>
    [BindProperty(SupportsGet = true)]
    public Guid? PresetId { get; set; }

    public IActionResult OnGet()
    {
        var servers = _serverManager.GetAllInstances();
        var all = _presetManager.GetAllPresets().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Presets = all.ForDecisions(servers, _engines).ToList();
        ChatPresets = all.ForChat(servers, _engines).ToList();
        ImagePresets = all.OfModality(servers, _engines, EngineModality.Image).ToList();

        // A preset that both chats and decides (granite-switch) stays here when asked for here.
        if (PresetId is Guid id && Presets.All(p => p.Id != id))
        {
            if (ImagePresets.Any(p => p.Id == id))
                return RedirectToPage("Generate", new { presetId = id });
            if (ChatPresets.Any(p => p.Id == id))
                return RedirectToPage("Index", new { presetId = id });
        }
        return Page();
    }
}
