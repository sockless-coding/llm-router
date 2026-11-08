using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Application.Pages.Features.Presets;

public class ActionsModel : PageModel
{
    private readonly IServerManager _serverManager;
    private readonly LRDbContext _context;
    private readonly IVllmRecipeClient _recipes;

    public ActionsModel(IServerManager serverManager, LRDbContext context, IVllmRecipeClient recipes)
    {
        _serverManager = serverManager;
        _context = context;
        _recipes = recipes;
    }

    /// <summary>The vLLM recipe list and hardware vocabulary, for the picker on the preset form.</summary>
    public Task<IActionResult> OnGetVllmCatalogAsync() =>
        RecipeResultAsync(async ct => await _recipes.GetCatalogAsync(ct));

    /// <summary>One recipe's choices.</summary>
    public Task<IActionResult> OnGetVllmRecipeAsync(string hfId) =>
        RecipeResultAsync(async ct => await _recipes.GetRecipeAsync(hfId, ct)
            ?? throw new InvalidOperationException($"There's no vLLM recipe for {hfId}."));

    /// <summary>
    /// What applying a recipe would put in the form, and what to read before saving. Only reads: nothing
    /// is stored until the user saves the preset, and nothing the site returned has been run.
    /// <paramref name="features"/> is comma-separated, <c>none</c> for all off; null keeps the recipe's defaults.
    /// </summary>
    public Task<IActionResult> OnGetVllmApplyAsync(string hfId, string? hardware, string? variant, string? strategy, string? features) =>
        RecipeResultAsync(async ct =>
        {
            var selected = features == "none" ? [] : features?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var rendering = await _recipes.RenderAsync(hfId, new VllmRecipeSelection(hardware, variant, strategy, selected), ct)
                ?? throw new InvalidOperationException($"There's no vLLM recipe for {hfId}.");
            var details = await _recipes.GetRecipeAsync(hfId, ct);
            var applied = VllmPresetSettings.Apply(rendering, details, selected);
            return new { applied.Model, applied.Settings, applied.Warnings, rendering.PageUrl, rendering.Hardware, rendering.Variant, rendering.Strategy };
        });

    private async Task<IActionResult> RecipeResultAsync<T>(Func<CancellationToken, Task<T>> action)
    {
        try
        {
            return new JsonResult(await action(HttpContext.RequestAborted));
        }
        catch (InvalidOperationException ex)
        {
            return new JsonResult(new { error = ex.Message }) { StatusCode = StatusCodes.Status502BadGateway };
        }
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
