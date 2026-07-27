using LR.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Servers;

public class EditModel : PageModel
{
    private readonly IServerManager _serverManager;

    [BindProperty]
    public Guid Id { get; set; }

    public Core.Models.ServerInstance? Server { get; set; }

    public EditModel(IServerManager serverManager)
    {
        _serverManager = serverManager;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        Server = await _serverManager.GetHealthAsync(Id);
        if (Server is null)
            return NotFound();
        return Page();
    }
}

