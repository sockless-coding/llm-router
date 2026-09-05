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

    public List<LlamaCppBuild> AvailableBuilds { get; set; } = new();

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
        ViewModel.EnvironmentSetupCommand = Server.Config?.EnvironmentSetupCommand ?? string.Empty;
        ViewModel.EngineBuildId = Server.Config?.EngineBuildId;

        AvailableBuilds = await _context.LlamaCppBuilds
            .Where(b => b.Status == EngineBuildStatus.Ready)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

        // Get the start command for display
        ViewModel.StartCommand = await _serverManager.GetStartCommandAsync(Id);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        AvailableBuilds = await _context.LlamaCppBuilds
            .Where(b => b.Status == EngineBuildStatus.Ready)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

        if (!ModelState.IsValid)
            return Page();

        Server = await _context.ServerInstances
            .Include(s => s.Config)
            .FirstOrDefaultAsync(s => s.Id == Id);

        if (Server is null)
            return NotFound();

        var boundBuild = ViewModel.EngineBuildId is { } bid
            ? await _context.LlamaCppBuilds.FindAsync(bid)
            : null;

        // A managed build supplies the folder path; only validate a manually-entered one.
        if (boundBuild is null
            && Server.Engine == ServerEngine.LlamaCpp
            && !string.IsNullOrWhiteSpace(ViewModel.LlamaCppExecutableFolderPath)
            && !Directory.Exists(ViewModel.LlamaCppExecutableFolderPath))
        {
            ModelState.AddModelError(nameof(ViewModel.LlamaCppExecutableFolderPath), $"The folder '{ViewModel.LlamaCppExecutableFolderPath}' does not exist.");
            return Page();
        }

        var configData = new BackendConfigData
        {
            LlamaCppExecutableFolderPath = boundBuild is not null
                ? boundBuild.InstallPath
                : string.IsNullOrWhiteSpace(ViewModel.LlamaCppExecutableFolderPath) ? null : ViewModel.LlamaCppExecutableFolderPath,
            CompanionAppPath = string.IsNullOrWhiteSpace(ViewModel.CompanionAppPath) ? null : ViewModel.CompanionAppPath,
            EnvironmentSetupCommand = string.IsNullOrWhiteSpace(ViewModel.EnvironmentSetupCommand) ? null : ViewModel.EnvironmentSetupCommand,
            EngineBuildId = boundBuild?.Id,
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
    public string? EnvironmentSetupCommand { get; set; }
    public string? StartCommand { get; set; }

    /// <summary>Optional managed build to bind this server to (auto-fills the folder path).</summary>
    public Guid? EngineBuildId { get; set; }
}
