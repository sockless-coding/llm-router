using System.Text.Json;

using LR.Core.Interfaces;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Servers;

[IgnoreAntiforgeryToken]
public class ServerActionsModel : PageModel
{
    private readonly IServerManager _serverManager;

    public ServerActionsModel(IServerManager serverManager)
    {
        _serverManager = serverManager;
    }

    [BindProperty(SupportsGet = false)]
    public Guid InstanceId { get; set; }

    /// <summary>
    /// The server command to execute (Start/Stop). Named "Command" to avoid
    /// conflicting with the built-in PageModel.Action property.
    /// </summary>
    [BindProperty(SupportsGet = false)]
    public string Command { get; set; } = string.Empty;

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            switch (Command.ToUpperInvariant())
            {
                case "START":
                    return await HandleStart();
                case "STOP":
                    return await HandleStop();
                default:
                    return JsonResult(new { success = false, message = $"Unknown action: {Command}" });
            }
        }
        catch (Exception ex)
        {
            return JsonResult(new { success = false, message = ex.Message });
        }
    }

    private async Task<IActionResult> HandleStart()
    {
        var started = await _serverManager.StartAsync(InstanceId);
        if (!started)
        {
            return JsonResult(new { success = false, message = "Server failed to start" });
        }
        return JsonResult(new { success = true, message = "Server started successfully" });
    }

    private async Task<IActionResult> HandleStop()
    {
        await _serverManager.StopAsync(InstanceId);
        return JsonResult(new { success = true, message = "Server stopped successfully" });
    }

    private IActionResult JsonResult(object data)
    {
        return Content(JsonSerializer.Serialize(data), "application/json");
    }
}

