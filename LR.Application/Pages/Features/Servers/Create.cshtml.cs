using LR.Core.Interfaces;
using LR.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Servers;

public class ServerCreateModel : PageModel
{
    private readonly IServerManager _serverManager;

    [BindProperty]
    public ServerCreateViewModel ViewModel { get; set; } = new();

    public ServerCreateModel(IServerManager serverManager)
    {
        _serverManager = serverManager;
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var backendType = Enum.Parse<BackendType>(ViewModel.BackendType, ignoreCase: true);
        await _serverManager.CreateInstanceAsync(ViewModel.Name, backendType, ViewModel.Port);

        return RedirectToPage("Index");
    }
}

public class ServerCreateViewModel
{
    public string Name { get; set; } = "";
    public string BackendType { get; set; } = "Cuda";
    public int? Port { get; set; }
}

