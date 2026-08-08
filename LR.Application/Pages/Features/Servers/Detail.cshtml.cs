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

    /// <summary>Wrapper process ID, if a wrapper is currently connected for this server. Diagnostics only.</summary>
    public int? WrapperPid { get; set; }

    /// <summary>Managed server process ID, if currently running. Diagnostics only.</summary>
    public int? ServerPid { get; set; }

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

            if (_serverManager.GetProvider(Server.Id) is IWrapperDiagnostics diagnostics)
            {
                WrapperPid = diagnostics.WrapperPid;
                ServerPid = diagnostics.ServerPid;
            }
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
