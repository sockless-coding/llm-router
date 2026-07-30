using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.Presets;

public class ActionsModel : PageModel
{
    private readonly IServerManager _serverManager;
    private readonly LRDbContext _context;

    public ActionsModel(IServerManager serverManager, LRDbContext context)
    {
        _serverManager = serverManager;
        _context = context;
    }

    [BindProperty]
    public Guid PresetId { get; set; }

    [BindProperty]
    public string? Command { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        return Command?.ToLowerInvariant() switch
        {
            "start" => await HandleStartAsync(),
            _ => BadRequest(new { success = false, message = $"Unknown command: {Command}" })
        };
    }

    private async Task<IActionResult> HandleStartAsync()
    {
        try
        {
            var preset = await _context.ModelPresets.FindAsync(PresetId);
            if (preset == null)
                return NotFound(new { success = false, message = "Preset not found." });

            var serverInstanceId = preset.ServerInstanceId;

            var instances = await _serverManager.GetAllInstancesAsync();
            var instance = instances.FirstOrDefault(i => i.Id == serverInstanceId);

            if (instance is null)
                return NotFound(new { success = false, message = "Server for this preset not found." });

            // Already running with this exact preset — nothing to do
            if (instance.Status == ServerStatus.Running && instance.ActivePresetId == PresetId)
                return JsonResult(new { success = true, message = $"Server '{instance.Name}' is already running with preset '{preset.Name}'." });

            bool started;
            if (instance.Status == ServerStatus.Running)
            {
                // Running but with a different preset — restart with the new one
                started = await _serverManager.RestartWithPresetAsync(serverInstanceId, PresetId);
            }
            else
            {
                // Idle or errored — start fresh with this preset
                started = await _serverManager.StartWithPresetAsync(serverInstanceId, PresetId);
            }

            return JsonResult(new { success = started, message = started ? $"Server '{instance.Name}' is starting with preset '{preset.Name}'." : "Failed to start server. Check the logs for details." });
        }
        catch (Exception ex)
        {
            return JsonResult(new { success = false, message = ex.Message });
        }
    }

    private IActionResult JsonResult(object data)
    {
        return Content(JsonSerializer.Serialize(data), "application/json");
    }
        
    
}
