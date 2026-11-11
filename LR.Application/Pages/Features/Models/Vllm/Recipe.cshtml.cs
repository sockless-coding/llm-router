using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Application.Pages.Features.Models.Vllm;

/// <summary>Writes or edits one recipe of a vLLM model: the settings a preset gets from it.</summary>
public class VllmRecipeEditModel : PageModel
{
    private readonly IVllmModelLibrary _library;
    private readonly IVllmRecipeClient _recipes;

    public VllmRecipeEditModel(IVllmModelLibrary library, IVllmRecipeClient recipes)
    {
        _library = library;
        _recipes = recipes;
    }

    /// <summary>Settings the editor has a field for; any other key is carried through unchanged.</summary>
    public static readonly IReadOnlyList<string> EditedKeys =
    [
        VllmPresetSettings.MaxModelLen, VllmPresetSettings.GpuMemoryUtilization, VllmPresetSettings.Dtype,
        VllmPresetSettings.MaxConcurrentRequests, VllmPresetSettings.ToolCallParser, VllmPresetSettings.ReasoningParser,
        VllmPresetSettings.AutoToolChoice, VllmPresetSettings.TrustRemoteCode, VllmPresetSettings.ExtraArgs, VllmPresetSettings.Environment,
    ];

    public sealed class RecipeInput
    {
        public Guid Id { get; set; }
        public Guid ModelId { get; set; }
        public string Name { get; set; } = "";
        public string? Hardware { get; set; }
        public string ServedModel { get; set; } = "";
        public double? VramGb { get; set; }
        public string? Warnings { get; set; }
        public string? Notes { get; set; }
        public Dictionary<string, string> Settings { get; set; } = new();
    }

    [BindProperty]
    public RecipeInput Input { get; set; } = new();

    public VllmModel VllmModel { get; private set; } = null!;
    public bool IsNew { get; private set; }
    public VllmRecipeOrigin Origin { get; private set; } = VllmRecipeOrigin.Custom;
    public IReadOnlyList<VllmHardwareProfile> HardwareProfiles { get; private set; } = [];
    public string? Error { get; private set; }

    public string Setting(string key) => Input.Settings.GetValueOrDefault(key) ?? "";
    public bool Flag(string key) => bool.TryParse(Setting(key), out var on) && on;

    public async Task<IActionResult> OnGetAsync(Guid? id, Guid? modelId, string? template, string? hardware, CancellationToken ct)
    {
        if (id is { } recipeId)
        {
            if (await _library.GetRecipeAsync(recipeId, ct) is not { } recipe)
                return NotFound();
            VllmModel = recipe.VllmModel!;
            Origin = recipe.Origin;
            Input = new RecipeInput
            {
                Id = recipe.Id, ModelId = recipe.VllmModelId, Name = recipe.Name, Hardware = recipe.Hardware, ServedModel = recipe.ServedModel,
                VramGb = recipe.VramGb, Warnings = recipe.Warnings, Notes = recipe.Notes, Settings = new(recipe.Settings),
            };
        }
        else if (modelId is { } mid && await _library.GetModelAsync(mid, ct) is { } model)
        {
            VllmModel = model;
            IsNew = true;
            Input = new RecipeInput { Id = Guid.NewGuid(), ModelId = model.Id, ServedModel = model.HfId, Hardware = string.IsNullOrWhiteSpace(hardware) ? null : hardware };
            if (template == "qwen38-b70")
            {
                Input.Name = "Arc Pro B70 · MTP, 100K context";
                Input.Hardware = "arc_pro_b70";
                Input.Settings = new(VllmDocker.Qwen38Preset);
                Input.Notes = "For an Intel (XPU, Docker) install and a Qwen3.8-27B GPTQ INT4 model: MTP with 4 speculative tokens, 100K context, low reasoning by default. "
                    + "Set the served model to the GPTQ INT4 repository or folder you run.";
            }
        }
        else
        {
            return NotFound();
        }

        await LoadHardwareAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var existing = await _library.GetRecipeAsync(Input.Id, ct);
        if ((existing?.VllmModel ?? await _library.GetModelAsync(Input.ModelId, ct)) is not { } model)
            return NotFound();
        VllmModel = model;
        IsNew = existing is null;
        Origin = existing?.Origin ?? VllmRecipeOrigin.Custom;

        try
        {
            var saved = await _library.SaveRecipeAsync(new VllmRecipe
            {
                Id = Input.Id,
                VllmModelId = model.Id,
                Name = Input.Name ?? "",
                Hardware = Input.Hardware,
                ServedModel = Input.ServedModel ?? "",
                VramGb = Input.VramGb,
                Warnings = Input.Warnings,
                Notes = Input.Notes,
                Settings = Input.Settings ?? new(),
            }, ct);
            TempData["Message"] = $"Saved '{saved.Name}'.";
            return Redirect($"/Features/Models/Vllm/Model?id={model.Id}#recipe-{saved.Id}");
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
            await LoadHardwareAsync(ct);
            return Page();
        }
    }

    private async Task LoadHardwareAsync(CancellationToken ct)
    {
        try
        {
            HardwareProfiles = (await _recipes.GetCatalogAsync(ct)).Hardware;
        }
        catch (InvalidOperationException)
        {
            HardwareProfiles = [];
        }
        // Keep a saved profile selectable even when the site's list doesn't have it (or didn't load).
        if (Input.Hardware is { Length: > 0 } hw && HardwareProfiles.All(h => h.Id != hw))
            HardwareProfiles = [.. HardwareProfiles, new VllmHardwareProfile(hw, hw, "", 0, 0)];
    }
}
