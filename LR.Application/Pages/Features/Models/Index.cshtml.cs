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
    public IReadOnlyList<ModelGroupViewModel> ModelGroups { get; set; } = new List<ModelGroupViewModel>();
    public Dictionary<Guid, int> PresetUsageCounts { get; set; } = new();

    [BindProperty]
    public string RootFolder { get; set; } = string.Empty;

    [BindProperty]
    public string? HuggingFaceApiToken { get; set; }

    [BindProperty]
    public bool ClearHuggingFaceApiToken { get; set; }

    /// <summary>Last characters of the saved token, or null when none is saved. Password inputs never
    /// render their value, so this is how the page shows that a token is set.</summary>
    public string? SavedTokenHint { get; set; }

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
        SavedTokenHint = TokenHint(settings.HuggingFaceApiToken);
        await LoadModelsAsync();
    }

    public async Task<IActionResult> OnPostSaveSettingsAsync()
    {
        try
        {
            // The token field always posts back blank unless a new token was typed, so blank keeps
            // the saved one; removing it takes the explicit checkbox.
            var savedToken = (await _settings.GetAsync()).HuggingFaceApiToken;
            var token = ClearHuggingFaceApiToken ? null
                : string.IsNullOrWhiteSpace(HuggingFaceApiToken) ? savedToken
                : HuggingFaceApiToken.Trim();
            await _settings.SaveAsync(RootFolder ?? string.Empty, token);
            StatusMessage = "Library settings saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save settings: {ex.Message}";
        }

        HuggingFaceApiToken = null;
        ClearHuggingFaceApiToken = false;
        ModelState.Clear();
        SavedTokenHint = TokenHint((await _settings.GetAsync()).HuggingFaceApiToken);

        await LoadModelsAsync();
        return Page();
    }

    private static string? TokenHint(string? token) =>
        string.IsNullOrWhiteSpace(token) ? null
        : token.Length > 8 ? $"{token[..3]}…{token[^4..]}"
        : "•••";

    private async Task LoadModelsAsync()
    {
        Models = await _modelLibrary.GetAllAsync();
        ModelGroups = ModelGroupViewModel.Build(Models);

        var allPresets = await _presetManager.GetAllPresetsAsync();
        PresetUsageCounts = allPresets
            .Where(p => p.ModelId.HasValue)
            .GroupBy(p => p.ModelId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
    }
}
