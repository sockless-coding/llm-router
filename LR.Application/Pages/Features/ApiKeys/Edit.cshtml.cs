using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.ApiKeys;

public class ApiKeyEditModel : PageModel
{
    private readonly IApiKeyManager _apiKeyManager;
    private readonly IPresetManager _presetManager;

    [BindProperty]
    public ApiKeyViewModel ViewModel { get; set; } = new();

    [BindProperty]
    public Guid Id { get; set; }

    public string KeyPrefix { get; set; } = "";
    public IReadOnlyList<ModelPreset> Presets { get; set; } = new List<ModelPreset>();

    public ApiKeyEditModel(IApiKeyManager apiKeyManager, IPresetManager presetManager)
    {
        _apiKeyManager = apiKeyManager;
        _presetManager = presetManager;
    }

    public async Task<IActionResult> OnGetAsync([FromQuery] Guid id)
    {
        var key = await _apiKeyManager.GetByIdAsync(id);
        if (key is null)
            return BadRequest($"API key with id {id} not found.");

        Presets = await _presetManager.GetAllPresetsAsync();
        Id = key.Id;
        KeyPrefix = key.KeyPrefix;
        ViewModel = new ApiKeyViewModel
        {
            Name = key.Name,
            IsEnabled = key.IsEnabled,
            AllowAllModels = key.AllowAllModels,
            SelectedPresetIds = key.AllowedPresets.Select(a => a.ModelPresetId).ToList()
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            Presets = await _presetManager.GetAllPresetsAsync();
            var existing = await _apiKeyManager.GetByIdAsync(Id);
            KeyPrefix = existing?.KeyPrefix ?? "";
            return Page();
        }

        var ok = await _apiKeyManager.UpdateAsync(
            Id,
            ViewModel.Name,
            ViewModel.IsEnabled,
            ViewModel.AllowAllModels,
            ViewModel.SelectedPresetIds ?? new List<Guid>());

        if (!ok)
            return BadRequest($"API key with id {Id} not found.");

        return RedirectToPage("Index");
    }
}
