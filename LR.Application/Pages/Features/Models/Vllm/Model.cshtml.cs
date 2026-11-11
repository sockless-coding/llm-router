using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Application.Pages.Features.Models.Vllm;

/// <summary>One vLLM model and its recipes: add one from recipes.vllm.ai, write one, refresh, copy or delete them.</summary>
public class VllmModelDetailsModel : PageModel
{
    private readonly IVllmModelLibrary _library;
    private readonly IVllmRecipeClient _recipes;
    private readonly IPresetManager _presets;

    public VllmModelDetailsModel(IVllmModelLibrary library, IVllmRecipeClient recipes, IPresetManager presets)
    {
        _library = library;
        _recipes = recipes;
        _presets = presets;
    }

    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    /// <summary>The hardware the list page was filtered to; preselected in the recipe site form.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Hardware { get; set; }

    public VllmModel VllmModel { get; private set; } = null!;

    /// <summary>The model's recipe on recipes.vllm.ai, or null when it has none or the site didn't answer.</summary>
    public VllmRecipeDetails? SiteRecipe { get; private set; }

    public string? SiteError { get; private set; }
    public IReadOnlyList<VllmHardwareProfile> HardwareProfiles { get; private set; } = [];
    public IReadOnlyList<VllmStrategy> Strategies { get; private set; } = [];

    /// <summary>Presets made from each recipe.</summary>
    public Dictionary<Guid, List<ModelPreset>> PresetsByRecipe { get; private set; } = new();

    [TempData]
    public string? Message { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public string HardwareLabel(string? id) =>
        id is null ? "Any hardware" : HardwareProfiles.FirstOrDefault(h => h.Id == id)?.DisplayName ?? id;

    /// <summary>The <c>vllm serve</c> arguments a preset from <paramref name="recipe"/> would start with, or why it wouldn't.</summary>
    public static (List<string>? Args, string? Error) Command(VllmRecipe recipe)
    {
        try
        {
            return (VllmPresetSettings.Validate(recipe.ServedModel, recipe.Settings), null);
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex.Message);
        }
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await _library.GetModelAsync(Id, ct) is not { } model)
            return NotFound();
        VllmModel = model;

        try
        {
            var catalog = await _recipes.GetCatalogAsync(ct);
            HardwareProfiles = catalog.Hardware;
            Strategies = catalog.Strategies;
            SiteRecipe = await _recipes.GetRecipeAsync(model.HfId);
        }
        catch (InvalidOperationException ex)
        {
            SiteError = ex.Message;
        }

        PresetsByRecipe = (await _presets.GetAllPresetsAsync())
            .Select(p => (Preset: p, Id: Guid.TryParse(p.EngineSettings.GetValueOrDefault(VllmPresetSettings.SavedRecipeId), out var id) ? id : (Guid?)null))
            .Where(x => x.Id is not null)
            .GroupBy(x => x.Id!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Preset).OrderBy(p => p.Name).ToList());
        return Page();
    }

    /// <param name="features">The features ticked. <paramref name="featuresShown"/> says the form had features to tick, so none ticked means all off.</param>
    public async Task<IActionResult> OnPostAddSiteRecipeAsync(string? variant, string? siteHardware, string? strategy, string[]? features, bool featuresShown, string? name, CancellationToken ct)
    {
        IReadOnlyList<string>? selected = featuresShown ? (features ?? []) : null;
        return await RunAsync(async () =>
        {
            var recipe = await _library.AddSiteRecipeAsync(Id, new VllmRecipeSelection(Blank(siteHardware), Blank(variant), Blank(strategy), selected), name, ct);
            return $"Saved '{recipe.Name}'. Read what it says to check, then create a preset from it.";
        });
    }

    public Task<IActionResult> OnPostRefreshAsync(Guid recipeId, CancellationToken ct) =>
        RunAsync(async () => $"Fetched '{(await _library.RefreshSiteRecipeAsync(recipeId, ct)).Name}' from recipes.vllm.ai again. Presets made from it keep their settings; apply it again in a preset to update one.");

    public Task<IActionResult> OnPostDuplicateAsync(Guid recipeId, CancellationToken ct) =>
        RunAsync(async () => (await _library.DuplicateRecipeAsync(recipeId, ct)) is { } copy ? $"Made '{copy.Name}'. Edit it to make it your own." : "That recipe is gone.");

    public Task<IActionResult> OnPostDeleteRecipeAsync(Guid recipeId, CancellationToken ct) =>
        RunAsync(async () => await _library.DeleteRecipeAsync(recipeId, ct) ? "Deleted the recipe. Presets made from it keep their settings." : "That recipe was already gone.");

    public Task<IActionResult> OnPostSaveNotesAsync(string? notes, CancellationToken ct) =>
        RunAsync(async () => await _library.UpdateModelNotesAsync(Id, notes, ct) ? "Saved the notes." : "That model is gone.");

    public async Task<IActionResult> OnPostDeleteModelAsync(CancellationToken ct)
    {
        var model = await _library.GetModelAsync(Id);
        await _library.DeleteModelAsync(Id);
        TempData[nameof(Message)] = model is null ? null : $"Removed {model.Name} and its recipes. Presets made from them keep their settings.";
        return RedirectToPage("Index");
    }

    private async Task<IActionResult> RunAsync(Func<Task<string>> action)
    {
        try
        {
            Message = await action();
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }
        return RedirectToPage(new { id = Id, hardware = Blank(Hardware) });
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
