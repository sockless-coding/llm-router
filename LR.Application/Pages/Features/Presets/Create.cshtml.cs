using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Presets;

public class PresetCreateModel : PageModel, IPresetFormPageModel
{
    private readonly IPresetManager _presetManager;
    private readonly IServerManager _serverManager;
    private readonly IModelLibrary _modelLibrary;
    private readonly IChatTemplateVariableExtractor _templateVariableExtractor;
    private readonly IVllmModelLibrary _vllmLibrary;

    [BindProperty]
    public PresetViewModel ViewModel { get; set; } = new();

    public IReadOnlyList<ServerInstance> Servers { get; set; } = new List<ServerInstance>();
    public IReadOnlyList<LocalModel> Models { get; set; } = new List<LocalModel>();
    public IReadOnlyList<LocalModel> MmprojCandidates { get; set; } = new List<LocalModel>();
    public IReadOnlyList<ModelPreset> FallbackCandidates { get; set; } = new List<ModelPreset>();

    // A new preset has nothing on disk to extract chat-template variables from yet; the
    // kwargs editor falls back to its client-side AJAX fetch once a model is picked.
    public IReadOnlyList<ChatTemplateVariable> DetectedTemplateVariables { get; set; } = Array.Empty<ChatTemplateVariable>();

    /// <summary>What to tell the user about the vLLM recipe the form was filled in from (<c>?vllmRecipe=</c>).</summary>
    public IReadOnlyList<string> StartingRecipeWarnings { get; private set; } = [];

    public PresetCreateModel(IPresetManager presetManager, IServerManager serverManager, IModelLibrary modelLibrary, IChatTemplateVariableExtractor templateVariableExtractor, IVllmModelLibrary vllmLibrary)
    {
        _vllmLibrary = vllmLibrary;
        _presetManager = presetManager;
        _serverManager = serverManager;
        _modelLibrary = modelLibrary;
        _templateVariableExtractor = templateVariableExtractor;
    }

    /// <param name="vllmRecipe">A saved vLLM recipe to start from: its settings and model are filled in, on the first vLLM server.</param>
    public async Task OnGetAsync(Guid? vllmRecipe)
    {
        Servers = _serverManager.GetAllInstances();
        FallbackCandidates = _presetManager.GetAllPresets();
        Models = await _modelLibrary.GetAllAsync();
        MmprojCandidates = Models.Where(m => ModelKindClassifier.Classify(m) == ModelType.Projector).ToList();

        if (vllmRecipe is { } recipeId && await _vllmLibrary.GetRecipeAsync(recipeId) is { } recipe)
        {
            ViewModel.ModelPath = recipe.ServedModel;
            ViewModel.EngineSettings = LR.Core.Services.EngineBuilds.VllmPresetSettings.ForPreset(recipe);
            ViewModel.Name = UniquePresetName(recipe.VllmModel?.HfId ?? recipe.ServedModel);
            if (Servers.FirstOrDefault(s => s.Engine == ServerEngine.Vllm) is { } server)
                ViewModel.ServerInstanceId = server.Id;
            StartingRecipeWarnings = recipe.WarningLines.ToList();
        }
    }

    private string UniquePresetName(string model)
    {
        var name = model.Split('/').Last().ToLowerInvariant();
        var taken = _presetManager.GetAllPresets().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (int i = 2; taken.Contains(candidate); i++)
            candidate = $"{name}-{i}";
        return candidate;
    }

    /// <summary>
    /// AJAX endpoint backing the ChatTemplateKwargs editor: given a registry model id, extracts
    /// the custom Jinja variables its chat template reads so the UI can offer them as fields
    /// instead of requiring hand-written JSON. Called from client script on model selection.
    /// </summary>
    public async Task<IActionResult> OnGetTemplateVariablesAsync(Guid? modelId)
    {
        if (modelId is null)
            return new JsonResult(Array.Empty<object>());

        var model = await _modelLibrary.GetByIdAsync(modelId.Value);
        var variables = _templateVariableExtractor.Extract(model?.ChatTemplate);
        return new JsonResult(variables.Select(v => new { name = v.Name, literalValues = v.LiteralValues }));
    }

    /// <summary>
    /// AJAX endpoint backing the live command-line preview: builds the exact llama.cpp args the
    /// current (unsaved) form state would launch with, resolving registry-linked model/draft
    /// model/mmproj picks against the model library the same way <see cref="LR.Core.Services.PresetManager"/>
    /// would on save. Deliberately skips ModelState validation — a preview should still render
    /// while required fields are mid-edit.
    /// </summary>
    public async Task<IActionResult> OnPostPreviewAsync()
    {
        var preset = new ModelPreset();
        PresetViewModelMapper.ApplyToEntity(ViewModel, preset);
        await PresetPreview.ResolveLinkedModelsAsync(preset, _modelLibrary);
        var engine = _serverManager.GetAllInstances().FirstOrDefault(s => s.Id == ViewModel.ServerInstanceId)?.Engine ?? ServerEngine.LlamaCpp;
        return new JsonResult(PresetPreview.Build(preset, engine));
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ViewModel.ModelId.HasValue && string.IsNullOrWhiteSpace(ViewModel.ModelPath))
            ModelState.AddModelError("ViewModel.ModelPath", "Select a model from the library, or enter a path manually.");

        // Library models live on one machine; the preset's server must run on that same machine.
        if (await PresetPreview.FindMachineMismatchAsync(ViewModel, _serverManager, _modelLibrary) is { } mismatch)
            ModelState.AddModelError("ViewModel.ModelId", mismatch);

        foreach (var alias in ModelAliasMatcher.ParseAliases(ViewModel.Aliases))
        {
            if (_presetManager.GetAllPresets().Any(p => p.Name == alias))
                ModelState.AddModelError("ViewModel.Aliases", $"'{alias}' is already another preset's name, so the alias would never match.");
        }

        if (!ModelState.IsValid)
        {
            Servers = _serverManager.GetAllInstances();
            FallbackCandidates = _presetManager.GetAllPresets();
            Models = await _modelLibrary.GetAllAsync();
            MmprojCandidates = Models.Where(m => ModelKindClassifier.Classify(m) == ModelType.Projector).ToList();
            return Page();
        }

        var preset = new ModelPreset { Id = Guid.NewGuid() };
        PresetViewModelMapper.ApplyToEntity(ViewModel, preset);

        await _presetManager.CreateAsync(preset);
        return RedirectToPage("Index");
    }
}
