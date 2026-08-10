using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.ApiKeys;

public class ApiKeysListModel : PageModel
{
    private readonly IApiKeyManager _apiKeyManager;
    private readonly IPresetManager _presetManager;

    public IReadOnlyList<ApiKey> Keys { get; set; } = new List<ApiKey>();
    public Dictionary<Guid, string> PresetNamesById { get; set; } = new();

    public ApiKeysListModel(IApiKeyManager apiKeyManager, IPresetManager presetManager)
    {
        _apiKeyManager = apiKeyManager;
        _presetManager = presetManager;
    }

    public async Task OnGetAsync()
    {
        Keys = await _apiKeyManager.GetAllAsync();
        PresetNamesById = (await _presetManager.GetAllPresetsAsync()).ToDictionary(p => p.Id, p => p.Name);
    }

    public async Task<IActionResult> OnPostDeleteAsync([FromQuery] Guid id)
    {
        var ok = await _apiKeyManager.DeleteAsync(id);
        if (!ok) return BadRequest("API key not found.");
        return new OkResult();
    }

    public async Task<IActionResult> OnPostToggleAsync([FromQuery] Guid id)
    {
        var key = await _apiKeyManager.GetByIdAsync(id);
        if (key is null) return BadRequest("API key not found.");

        var ok = await _apiKeyManager.UpdateAsync(id, key.Name, !key.IsEnabled, key.AllowAllModels,
            key.AllowedPresets.Select(a => a.ModelPresetId));
        if (!ok) return BadRequest("Failed to update API key.");

        return new OkResult();
    }

    public async Task<IActionResult> OnPostRegenerateAsync([FromQuery] Guid id)
    {
        var result = await _apiKeyManager.RegenerateAsync(id);
        if (result is null) return BadRequest("API key not found.");

        return new JsonResult(new { rawKey = result.Value.RawKey });
    }
}
