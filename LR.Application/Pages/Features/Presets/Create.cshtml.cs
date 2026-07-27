using LR.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Presets;

public class PresetCreateModel : PageModel
{
    private readonly IPresetManager _presetManager;
    private readonly IServerManager _serverManager;

    [BindProperty]
    public PresetCreateViewModel ViewModel { get; set; } = new();

    public IReadOnlyList<Core.Models.ServerInstance> Servers { get; set; } = new List<Core.Models.ServerInstance>();

    public PresetCreateModel(IPresetManager presetManager, IServerManager serverManager)
    {
        _presetManager = presetManager;
        _serverManager = serverManager;
    }

    public void OnGet()
    {
        Servers = _serverManager.GetAllInstances();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var preset = new Core.Models.ModelPreset
        {
            Id = Guid.NewGuid(),
            ServerInstanceId = ViewModel.ServerInstanceId,
            Name = ViewModel.Name,
            ModelPath = ViewModel.ModelPath,
            ContextLength = ViewModel.ContextLength,
            GpuLayers = ViewModel.GpuLayers,
        };

        await _presetManager.CreateAsync(preset);
        return RedirectToPage("Index");
    }
}

public class PresetCreateViewModel
{
    public Guid ServerInstanceId { get; set; }
    public string Name { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public int ContextLength { get; set; }
    public int GpuLayers { get; set; }
}

