using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Servers;

public class EditModel : PageModel
{
    private readonly IServerManager _serverManager;
    private readonly LRDbContext _context;

    /// <summary>
    /// Bound from the query string (e.g., ?Id=...). SupportsGet enables binding on GET requests.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public ServerInstance? Server { get; set; }

    [BindProperty]
    public EditViewModel ViewModel { get; set; } = new();

    public EditModel(IServerManager serverManager, LRDbContext context)
    {
        _serverManager = serverManager;
        _context = context;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        Server = await _context.ServerInstances
            .Include(s => s.Config)
            .FirstOrDefaultAsync(s => s.Id == Id);

        if (Server is null)
            return NotFound();

        ViewModel.LlamaCppExecutableFolderPath = Server.Config?.LlamaCppExecutableFolderPath ?? string.Empty;
        ViewModel.CompanionAppPath = Server.Config?.CompanionAppPath ?? string.Empty;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        Server = await _context.ServerInstances
            .Include(s => s.Config)
            .FirstOrDefaultAsync(s => s.Id == Id);

        if (Server is null)
            return NotFound();

        // Validate llama.cpp folder path exists
        if (Server.Engine == ServerEngine.LlamaCpp && !string.IsNullOrWhiteSpace(ViewModel.LlamaCppExecutableFolderPath))
        {
            if (!Directory.Exists(ViewModel.LlamaCppExecutableFolderPath))
            {
                ModelState.AddModelError(nameof(ViewModel.LlamaCppExecutableFolderPath), $"The folder '{ViewModel.LlamaCppExecutableFolderPath}' does not exist.");
                return Page();
            }
        }

        var configData = new BackendConfigData
        {
            LlamaCppExecutableFolderPath = string.IsNullOrWhiteSpace(ViewModel.LlamaCppExecutableFolderPath) ? null : ViewModel.LlamaCppExecutableFolderPath,
            CompanionAppPath = string.IsNullOrWhiteSpace(ViewModel.CompanionAppPath) ? null : ViewModel.CompanionAppPath,
        };

        await _serverManager.UpdateBackendConfigAsync(Id, configData);

        // Reload server with updated config
        Server = await _context.ServerInstances
            .Include(s => s.Config)
            .FirstOrDefaultAsync(s => s.Id == Id);

        return Page();
    }
}

public class EditViewModel
{
    public string? LlamaCppExecutableFolderPath { get; set; }
    public string? CompanionAppPath { get; set; }
}
