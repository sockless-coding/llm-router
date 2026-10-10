using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Servers;

public partial class EditModel : PageModel
{
    private readonly IServerManager _serverManager;
    private readonly LRDbContext _context;
    private readonly IEngineCatalog _engines;

    public List<EngineBuild> AvailableBuilds { get; set; } = new();

    /// <summary>
    /// Bound from the query string (e.g., ?Id=...). SupportsGet enables binding on GET requests.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public ServerInstance? Server { get; set; }

    /// <summary>The server's engine, or null if it has no registered provider.</summary>
    public IEngineDescriptor? Engine => Server is null ? null : _engines.Get(Server.Engine);

    [BindProperty]
    public EditViewModel ViewModel { get; set; } = new();

    public EditModel(IServerManager serverManager, LRDbContext context, IEngineCatalog engines)
    {
        _serverManager = serverManager;
        _context = context;
        _engines = engines;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        Server = await _context.ServerInstances
            .Include(s => s.Config)
            .FirstOrDefaultAsync(s => s.Id == Id);

        if (Server is null)
            return NotFound();

        ViewModel.InstallFolderPath = Server.Config?.InstallFolderPath ?? string.Empty;
        ViewModel.CompanionAppPath = Server.Config?.CompanionAppPath ?? string.Empty;
        ViewModel.EnvironmentSetupCommand = Server.Config?.EnvironmentSetupCommand ?? string.Empty;
        ViewModel.EngineBuildId = Server.Config?.EngineBuildId;

        AvailableBuilds = await LoadAvailableBuildsAsync(Server.Engine);

        // Get the start command for display
        ViewModel.StartCommand = await _serverManager.GetStartCommandAsync(Id);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Server = await _context.ServerInstances
            .Include(s => s.Config)
            .FirstOrDefaultAsync(s => s.Id == Id);

        if (Server is null)
            return NotFound();

        AvailableBuilds = await LoadAvailableBuildsAsync(Server.Engine);

        if (!ModelState.IsValid)
            return Page();

        var boundBuild = Engine is { SupportsManagedBuilds: true } && ViewModel.EngineBuildId is { } bid
            ? AvailableBuilds.FirstOrDefault(b => b.Id == bid)
            : null;

        // A managed build supplies the folder path; only validate a manually-entered one.
        if (boundBuild is null
            && Engine is not null
            && !string.IsNullOrWhiteSpace(ViewModel.InstallFolderPath)
            && Engine.ValidateInstallFolder(ViewModel.InstallFolderPath) is { } folderError)
        {
            ModelState.AddModelError(nameof(ViewModel.InstallFolderPath), folderError);
            return Page();
        }

        var configData = new BackendConfigData
        {
            InstallFolderPath = boundBuild is not null
                ? boundBuild.InstallPath
                : string.IsNullOrWhiteSpace(ViewModel.InstallFolderPath) ? null : ViewModel.InstallFolderPath,
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

public partial class EditModel
{
    /// <summary>Ready installs of <paramref name="engine"/> — the only ones a server of that engine can bind to.</summary>
    private Task<List<EngineBuild>> LoadAvailableBuildsAsync(ServerEngine engine) =>
        _context.EngineBuilds
            .Where(b => b.Status == EngineBuildStatus.Ready && b.Engine == engine)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();
}

public class EditViewModel
{
    public string? InstallFolderPath { get; set; }
    public string? CompanionAppPath { get; set; }
    public string? EnvironmentSetupCommand { get; set; }
    public string? StartCommand { get; set; }

    /// <summary>Optional managed build to bind this server to (auto-fills the folder path; llama.cpp only).</summary>
    public Guid? EngineBuildId { get; set; }
}
