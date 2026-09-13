using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Presets;

public class PresetEditModel : PageModel, IPresetFormPageModel
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
    public IReadOnlyList<ChatTemplateVariable> DetectedTemplateVariables { get; set; } = Array.Empty<ChatTemplateVariable>();

    public PresetEditModel(IPresetManager presetManager, IServerManager serverManager, IModelLibrary modelLibrary, IChatTemplateVariableExtractor templateVariableExtractor)
    {
        _presetManager = presetManager;
        _serverManager = serverManager;
        _modelLibrary = modelLibrary;
        _templateVariableExtractor = templateVariableExtractor;
    }

    public async Task<IActionResult> OnGetAsync([FromQuery] Guid Id)
    {
        var preset = await _presetManager.GetByIdAsync(Id);
        if (preset is null)
            return BadRequest($"Preset with id {Id} not found.");

        Servers = _serverManager.GetAllInstances();
        Models = await _modelLibrary.GetAllAsync();
        MmprojCandidates = Models.Where(m => ModelKindClassifier.Classify(m) == ModelFileKind.MultimodalProjector).ToList();
        PresetViewModelMapper.ToViewModel(preset, ViewModel);
        DetectedTemplateVariables = _templateVariableExtractor.Extract(preset.GgufChatTemplate);

        return Page();
    }

    /// <summary>
    /// AJAX endpoint backing the ChatTemplateKwargs editor: given a registry model id, extracts
    /// the custom Jinja variables its chat template reads so the UI can refresh the detected
    /// fields when the user switches the linked model. Called from client script.
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
    /// AJAX endpoint backing the live command-line preview — see the identical handler on
    /// <see cref="PresetCreateModel"/> for details.
    /// </summary>
    public async Task<IActionResult> OnPostPreviewAsync()
    {
        var preset = new ModelPreset();
        PresetViewModelMapper.ApplyToEntity(ViewModel, preset);
        await PresetPreview.ResolveLinkedModelsAsync(preset, _modelLibrary);
        return new JsonResult(PresetPreview.Build(preset));
    }

    public async Task<IActionResult> OnPostAsync([FromQuery] Guid Id)
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

        var existing = await _presetManager.GetByIdAsync(Id);
        if (existing is null)
            return NotFound();

        PresetViewModelMapper.ApplyToEntity(ViewModel, existing);

        await _presetManager.UpdateAsync(Id, existing);

        return RedirectToPage("Index");
    }
}
