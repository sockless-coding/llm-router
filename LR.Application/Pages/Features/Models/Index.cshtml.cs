using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.Models;

public class ModelsIndexModel : PageModel
{
    private readonly IModelLibrary _modelLibrary;
    private readonly IPresetManager _presetManager;
    private readonly IModelLibrarySettingsService _settings;

    public IReadOnlyList<LocalModel> Models { get; set; } = new List<LocalModel>();
    public Dictionary<Guid, int> PresetUsageCounts { get; set; } = new();

    [BindProperty]
    public string RootFolder { get; set; } = string.Empty;

    [BindProperty]
    public string? HuggingFaceApiToken { get; set; }

    public string? StatusMessage { get; set; }

    public ModelsIndexModel(IModelLibrary modelLibrary, IPresetManager presetManager, IModelLibrarySettingsService settings)
    {
        _modelLibrary = modelLibrary;
        _presetManager = presetManager;
        _settings = settings;
    }

    public async Task OnGetAsync()
    {
        var settings = await _settings.GetAsync();
        RootFolder = settings.RootFolder;
        HuggingFaceApiToken = settings.HuggingFaceApiToken;
        await LoadModelsAsync();
    }

    public async Task<IActionResult> OnPostSaveSettingsAsync()
    {
        try
        {
            await _settings.SaveAsync(RootFolder ?? string.Empty, HuggingFaceApiToken);
            StatusMessage = "Library settings saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save settings: {ex.Message}";
        }

        await LoadModelsAsync();
        return Page();
    }

    private async Task LoadModelsAsync()
    {
        Models = await _modelLibrary.GetAllAsync();

        var allPresets = await _presetManager.GetAllPresetsAsync();
        PresetUsageCounts = allPresets
            .Where(p => p.ModelId.HasValue)
            .GroupBy(p => p.ModelId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
    }
}
