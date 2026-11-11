using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Core.Services.EngineBuilds;

namespace LR.Application.Pages.Features.Models.Vllm;

/// <summary>
/// The vLLM models in the library, each with its saved recipes, filterable by hardware; and the search
/// that adds one from Hugging Face or recipes.vllm.ai.
/// </summary>
public class VllmModelsIndexModel : PageModel
{
    private readonly IVllmModelLibrary _library;
    private readonly IVllmRecipeClient _recipes;
    private readonly IHuggingFaceClient _hub;
    private readonly IPresetManager _presets;
    private readonly IComputeDeviceService _devices;
    private readonly ILogger<VllmModelsIndexModel> _logger;

    public VllmModelsIndexModel(IVllmModelLibrary library, IVllmRecipeClient recipes, IHuggingFaceClient hub, IPresetManager presets,
        IComputeDeviceService devices, ILogger<VllmModelsIndexModel> logger)
    {
        _library = library;
        _recipes = recipes;
        _hub = hub;
        _presets = presets;
        _devices = devices;
        _logger = logger;
    }

    /// <summary>The hardware profile to show recipes for (<c>?hardware=arc_pro_b70</c>); null shows every recipe.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Hardware { get; set; }

    public IReadOnlyList<VllmModel> Models { get; private set; } = [];
    public IReadOnlyList<VllmHardwareProfile> HardwareProfiles { get; private set; } = [];

    /// <summary>Profiles that look like this machine's GPUs, best match first.</summary>
    public IReadOnlyList<VllmHardwareProfile> DetectedHardware { get; private set; } = [];

    public string? CatalogError { get; private set; }

    /// <summary>Presets made from each recipe, by recipe id.</summary>
    public Dictionary<Guid, int> PresetCounts { get; private set; } = new();

    [TempData]
    public string? Message { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    /// <summary>Whether a recipe runs on the filtered hardware: made for it, or for any hardware.</summary>
    public bool Matches(VllmRecipe recipe) =>
        Hardware is null || recipe.Hardware is null || string.Equals(recipe.Hardware, Hardware, StringComparison.OrdinalIgnoreCase);

    public IEnumerable<VllmModel> VisibleModels =>
        Hardware is null ? Models : Models.Where(m => m.Recipes.Any(Matches));

    public string HardwareLabel(string? id) =>
        id is null ? "Any hardware" : HardwareProfiles.FirstOrDefault(h => h.Id == id)?.DisplayName ?? id;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Hardware = string.IsNullOrWhiteSpace(Hardware) ? null : Hardware.Trim();
        Models = await _library.GetModelsAsync(ct);
        try
        {
            HardwareProfiles = (await _recipes.GetCatalogAsync(ct)).Hardware;
        }
        catch (InvalidOperationException ex)
        {
            CatalogError = ex.Message;
            // The saved recipes still name their hardware; filtering by those works offline.
            HardwareProfiles = Models.SelectMany(m => m.Recipes).Select(r => r.Hardware).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Select(id => new VllmHardwareProfile(id, id, "", 0, 0)).ToList();
        }

        try
        {
            DetectedHardware = VllmHardwareMatcher.Match(await _devices.GetDevicesAsync(ct), HardwareProfiles);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not list compute devices for the vLLM hardware suggestion.");
        }

        PresetCounts = (await _presets.GetAllPresetsAsync())
            .Select(p => p.EngineSettings.GetValueOrDefault(VllmPresetSettings.SavedRecipeId))
            .Where(id => Guid.TryParse(id, out _))
            .GroupBy(id => Guid.Parse(id!))
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Models to add: recipes.vllm.ai's recipes matching <paramref name="q"/> (only those with a command
    /// for <paramref name="hardware"/>, when given), and Hugging Face repositories matching it that have
    /// no recipe. With no query but a hardware, every recipe for that hardware.
    /// </summary>
    public async Task<IActionResult> OnGetSearchAsync(string? q, string? hardware, CancellationToken ct)
    {
        q = q?.Trim() ?? "";
        hardware = string.IsNullOrWhiteSpace(hardware) ? null : hardware.Trim();
        if (q.Length == 0 && hardware is null)
            return new JsonResult(new { recipes = Array.Empty<object>(), hub = Array.Empty<object>() });

        var added = (await _library.GetModelsAsync(ct)).Select(m => m.HfId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? recipeError = null, hubError = null;

        IReadOnlyList<VllmRecipeSummary> siteModels = [];
        IReadOnlyDictionary<string, IReadOnlyList<string>>? index = null;
        try
        {
            siteModels = (await _recipes.GetCatalogAsync(ct)).Models;
            if (hardware is not null)
                index = await _recipes.GetHardwareIndexAsync(ct);
        }
        catch (InvalidOperationException ex)
        {
            recipeError = ex.Message;
        }

        bool Contains(string? text) => q.Length == 0 || (text?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
        var recipes = siteModels
            .Where(m => Contains(m.HfId) || Contains(m.Title) || Contains(m.Provider))
            .Where(m => index is null || (index.TryGetValue(m.HfId, out var hw) && hw.Contains(hardware!, StringComparer.OrdinalIgnoreCase)))
            .Take(60)
            .Select(m => new
            {
                hfId = m.HfId,
                title = m.Title,
                provider = m.Provider,
                hardware = index is not null && index.TryGetValue(m.HfId, out var hw) ? hw : null,
                added = added.Contains(m.HfId),
            })
            .ToList();

        var hub = new List<object>();
        if (q.Length > 0)
        {
            try
            {
                var withRecipe = siteModels.Select(m => m.HfId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var repo in await _hub.SearchModelsAsync(q, HfModelFormat.Safetensors, null, 20, ct))
                {
                    if (withRecipe.Contains(repo.Id)) continue;
                    hub.Add(new { hfId = repo.Id, author = repo.Author, downloads = repo.Downloads, likes = repo.Likes, added = added.Contains(repo.Id) });
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
            {
                hubError = $"Couldn't search Hugging Face: {ex.Message}";
            }
        }

        return new JsonResult(new { recipes, hub, recipeError, hubError });
    }

    public async Task<IActionResult> OnPostAddAsync(string hfId, string? hardware, CancellationToken ct)
    {
        try
        {
            var model = await _library.AddModelAsync(hfId, ct);
            return RedirectToPage("Model", new { id = model.Id, hardware = string.IsNullOrWhiteSpace(hardware) ? null : hardware });
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return RedirectToPage(new { hardware = string.IsNullOrWhiteSpace(hardware) ? null : hardware });
        }
    }
}
