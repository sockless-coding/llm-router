using LR.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Servers;

public class ServerDetailModel : PageModel
{
    private readonly IServerManager _serverManager;
    private readonly IServerLogService _logService;

    public Core.Models.ServerInstance? Server { get; set; }
    public IReadOnlyList<Core.Models.ServerLog> Logs { get; set; } = new List<Core.Models.ServerLog>();

    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public ServerDetailModel(IServerManager serverManager, IServerLogService logService)
    {
        _serverManager = serverManager;
        _logService = logService;
    }

    public async Task OnGetAsync()
    {
        var instances = await _serverManager.GetAllInstancesAsync();
        Server = instances.FirstOrDefault(s => s.Id == Id);

        if (Server != null)
        {
            Logs = await _logService.GetLogsAsync(Server.Id, 200);
        }
    }

    public async Task<JsonResult> OnPostClearLogsAsync()
    {
        if (!Id.Equals(Guid.Empty))
        {
            await _logService.ClearLogsAsync(Id);
        }
        return new JsonResult(new { success = true });
    }
}
