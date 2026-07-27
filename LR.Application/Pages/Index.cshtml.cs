using LR.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages;

public class DashboardModel : PageModel
{
    private readonly IServerManager _serverManager;

    public IReadOnlyList<Core.Models.ServerInstance> Servers { get; set; } = new List<Core.Models.ServerInstance>();

    public DashboardModel(IServerManager serverManager)
    {
        _serverManager = serverManager;
    }

    public void OnGet()
    {
        Servers = _serverManager.GetAllInstances();
    }
}
