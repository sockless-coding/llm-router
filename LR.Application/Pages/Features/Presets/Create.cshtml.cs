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

    [BindProperty]
    public PresetViewModel ViewModel { get; set; } = new();

    public IReadOnlyList<ServerInstance> Servers { get; set; } = new List<ServerInstance>();
    public IReadOnlyList<LocalModel> Models { get; set; } = new List<LocalModel>();
    public IReadOnlyList<LocalModel> MmprojCandidates { get; set; } = new List<LocalModel>();

    // A new preset has nothing on disk to extract chat-template variables from yet; the
    // kwargs editor falls back to its client-side AJAX fetch once a model is picked.
    public IReadOnlyList<ChatTemplateVariable> DetectedTemplateVariables { get; set; } = Array.Empty<ChatTemplateVariable>();

    public PresetCreateModel(IPresetManager presetManager, IServerManager serverManager, IModelLibrary modelLibrary, IChatTemplateVariableExtractor templateVariableExtractor)
    {
        _presetManager = presetManager;
        _serverManager = serverManager;
        _modelLibrary = modelLibrary;
        _templateVariableExtractor = templateVariableExtractor;
    }

    public async Task OnGetAsync()
    {
        Servers = _serverManager.GetAllInstances();
        Models = await _modelLibrary.GetAllAsync();
        MmprojCandidates = Models.Where(m => ModelKindClassifier.Classify(m) == ModelFileKind.MultimodalProjector).ToList();
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
        return new JsonResult(PresetPreview.Build(preset));
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ViewModel.ModelId.HasValue && string.IsNullOrWhiteSpace(ViewModel.ModelPath))
            ModelState.AddModelError("ViewModel.ModelPath", "Select a model from the library, or enter a path manually.");

        if (!ModelState.IsValid)
        {
            Servers = _serverManager.GetAllInstances();
            Models = await _modelLibrary.GetAllAsync();
            MmprojCandidates = Models.Where(m => ModelKindClassifier.Classify(m) == ModelFileKind.MultimodalProjector).ToList();
            return Page();
        }

        var preset = new ModelPreset { Id = Guid.NewGuid() };
        PresetViewModelMapper.ApplyToEntity(ViewModel, preset);

        await _presetManager.CreateAsync(preset);
        return RedirectToPage("Index");
    }
}
