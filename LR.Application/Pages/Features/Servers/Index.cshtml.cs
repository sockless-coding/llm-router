using LR.Core.Interfaces;
using LR.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Servers;

public class ServersListModel : PageModel
{
    private readonly IServerManager _serverManager;

    public IReadOnlyList<Core.Models.ServerInstance> Servers { get; set; } = new List<Core.Models.ServerInstance>();

    public ServersListModel(IServerManager serverManager)
    {
        _serverManager = serverManager;
    }

    public void OnGet()
    {
        Servers = _serverManager.GetAllInstances();
    }

    [BindProperty(SupportsGet = true)]
    public Guid? InstanceId { get; set; }

    public async Task<JsonResult> OnPostGetStartCommandAsync()
    {
        if (!InstanceId.HasValue)
            return new JsonResult(new { command = (string?)null });

        var command = await _serverManager.GetStartCommandAsync(InstanceId.Value);
        return new JsonResult(new { command });
    }

    /// <summary>
    /// Deletes a server along with its presets, config, logs and stats (cascade). A running server
    /// is stopped first; one mid-transition is refused since its background task still owns the row.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (!InstanceId.HasValue)
            return BadRequest("Missing server id.");

        var instance = await _serverManager.GetHealthAsync(InstanceId.Value);
        if (instance is null)
            return NotFound("Server not found.");

        if (instance.Status is ServerStatus.Starting or ServerStatus.Stopping or ServerStatus.Reconnecting)
            return new ConflictObjectResult($"Server is {instance.Status.ToString().ToLowerInvariant()} — wait for it to settle before deleting.");

        await _serverManager.RemoveInstanceAsync(InstanceId.Value);
        return new OkResult();
    }
}

