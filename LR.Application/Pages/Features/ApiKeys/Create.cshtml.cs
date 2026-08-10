using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.ApiKeys;

public class ApiKeyCreateModel : PageModel
{
    private readonly IApiKeyManager _apiKeyManager;
    private readonly IPresetManager _presetManager;

    [BindProperty]
    public ApiKeyViewModel ViewModel { get; set; } = new();

    public IReadOnlyList<ModelPreset> Presets { get; set; } = new List<ModelPreset>();

    /// <summary>
    /// Set after a successful create — the raw key, shown exactly once. Non-null means the form
    /// should render the reveal box instead of the input fields.
    /// </summary>
    public string? CreatedRawKey { get; set; }
    public string? CreatedKeyName { get; set; }

    public ApiKeyCreateModel(IApiKeyManager apiKeyManager, IPresetManager presetManager)
    {
        _apiKeyManager = apiKeyManager;
        _presetManager = presetManager;
    }

    public async Task OnGetAsync()
    {
        Presets = await _presetManager.GetAllPresetsAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            Presets = await _presetManager.GetAllPresetsAsync();
            return Page();
        }

        var (key, rawKey) = await _apiKeyManager.CreateAsync(
            ViewModel.Name,
            ViewModel.AllowAllModels,
            ViewModel.SelectedPresetIds ?? new List<Guid>());

        CreatedRawKey = rawKey;
        CreatedKeyName = key.Name;
        Presets = await _presetManager.GetAllPresetsAsync();
        return Page();
    }
}

public class ApiKeyViewModel
{
    [Required, MaxLength(128)]
    public string Name { get; set; } = "";

    public bool IsEnabled { get; set; } = true;

    public bool AllowAllModels { get; set; } = true;

    public List<Guid>? SelectedPresetIds { get; set; }
}
