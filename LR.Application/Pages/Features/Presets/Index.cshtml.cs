using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Presets;

public class PresetsListModel : PageModel
{
    private readonly LRDbContext _context;
    private readonly IPresetManager _presetManager;
    private readonly IServerManager _serverManager;

    public IReadOnlyList<Core.Models.ModelPreset> Presets { get; set; } = new List<Core.Models.ModelPreset>();
    public IReadOnlyList<Core.Models.ServerInstance> Servers { get; set; } = new List<Core.Models.ServerInstance>();

    public PresetsListModel(LRDbContext context, IPresetManager presetManager, IServerManager serverManager)
    {
        _context = context;
        _presetManager = presetManager;
        _serverManager = serverManager;
    }

    public async Task OnGetAsync([FromQuery] Guid? serverId = null)
    {
        if (serverId.HasValue && serverId.Value != Guid.Empty)
            Presets = await _presetManager.GetByServerInstanceIdAsync(serverId.Value);
        else
            // No filter — get all presets from DB directly
            Presets = (await _context.ModelPresets.ToListAsync()).AsReadOnly();

        Servers = _serverManager.GetAllInstances();
    }

    public async Task<IActionResult> OnPostDeleteAsync([FromQuery] Guid id)
    {
        var ok = await _presetManager.DeleteAsync(id);
        if (!ok)
            return BadRequest("Preset not found.");
        return new OkResult();
    }
}

