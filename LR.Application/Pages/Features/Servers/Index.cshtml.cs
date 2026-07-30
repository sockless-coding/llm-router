using LR.Core.Interfaces;
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
}

