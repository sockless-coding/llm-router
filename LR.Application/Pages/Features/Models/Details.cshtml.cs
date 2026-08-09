using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.Models;

public class ModelsDetailsModel : PageModel
{
    private readonly IModelLibrary _modelLibrary;
    private readonly IPresetManager _presetManager;

    public LocalModel LocalModel { get; set; } = null!;
    public IReadOnlyList<ModelPreset> UsedByPresets { get; set; } = new List<ModelPreset>();
    public Dictionary<string, object>? KvPairs { get; set; }

    public ModelsDetailsModel(IModelLibrary modelLibrary, IPresetManager presetManager)
    {
        _modelLibrary = modelLibrary;
        _presetManager = presetManager;
    }

    public async Task<IActionResult> OnGetAsync([FromQuery] Guid id)
    {
        var model = await _modelLibrary.GetByIdAsync(id);
        if (model is null)
            return NotFound();

        LocalModel = model;

        var allPresets = await _presetManager.GetAllPresetsAsync();
        UsedByPresets = allPresets.Where(p => p.ModelId == id).ToList();

        if (!string.IsNullOrEmpty(model.AllKvPairsJson))
        {
            try { KvPairs = JsonSerializer.Deserialize<Dictionary<string, object>>(model.AllKvPairsJson); }
            catch { KvPairs = null; }
        }

        return Page();
    }
}
