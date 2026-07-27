using LR.Core.Interfaces;
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
}

