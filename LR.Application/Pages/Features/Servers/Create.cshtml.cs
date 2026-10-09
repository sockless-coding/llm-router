using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Servers;

public class ServerCreateModel : PageModel
{
    private readonly IServerManager _serverManager;
    private readonly LRDbContext _context;

    [BindProperty]
    public ServerCreateViewModel ViewModel { get; set; } = new();

    /// <summary>
    /// Available server engines for the dropdown — only engines with a registered provider.
    /// </summary>
    public IReadOnlyList<IEngineDescriptor> Engines { get; }

    /// <summary>
    /// Ready managed installs (of every engine) the new server can be bound to — the form only
    /// shows the selected engine's.
    /// </summary>
    public List<EngineBuild> AvailableBuilds { get; set; } = new();

    public ServerCreateModel(IServerManager serverManager, LRDbContext context, IEngineCatalog engines)
    {
        _serverManager = serverManager;
        _context = context;
        Engines = engines.All;
    }

    public async Task OnGetAsync()
    {
        await LoadAvailableBuildsAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadAvailableBuildsAsync();

        if (!ModelState.IsValid)
            return Page();

        var descriptor = Enum.TryParse<ServerEngine>(ViewModel.Engine, ignoreCase: true, out var engine)
            ? Engines.FirstOrDefault(e => e.Engine == engine)
            : null;
        if (descriptor is null)
        {
            ModelState.AddModelError(nameof(ViewModel.Engine), "Select a supported server engine.");
            return Page();
        }

        EngineBuild? boundBuild = null;
        if (descriptor.SupportsManagedBuilds && ViewModel.EngineBuildId is { } buildId)
        {
            boundBuild = await _context.EngineBuilds.FindAsync(buildId);
            if (boundBuild is null || boundBuild.Engine != engine)
            {
                ModelState.AddModelError(nameof(ViewModel.EngineBuildId), boundBuild is null
                    ? "The selected build no longer exists."
                    : $"That install isn't a {descriptor.DisplayName} install.");
                return Page();
            }
        }

        // A managed build supplies the folder path; otherwise a manually-entered one is required.
        if (boundBuild is null)
        {
            if (string.IsNullOrWhiteSpace(ViewModel.InstallFolderPath))
            {
                ModelState.AddModelError(nameof(ViewModel.InstallFolderPath), descriptor.SupportsManagedBuilds
                    ? $"Select a managed build or enter a folder path for {descriptor.DisplayName}."
                    : $"Enter the folder path for {descriptor.DisplayName}.");
                return Page();
            }

            if (descriptor.ValidateInstallFolder(ViewModel.InstallFolderPath) is { } folderError)
            {
                ModelState.AddModelError(nameof(ViewModel.InstallFolderPath), folderError);
                return Page();
            }
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

        await _serverManager.CreateInstanceAsync(ViewModel.Name, engine, configData, ViewModel.Port);

        TempData["SuccessMessage"] = $"Server \"{ViewModel.Name}\" created successfully.";
        return RedirectToPage("Index");
    }

    private async Task LoadAvailableBuildsAsync()
    {
        AvailableBuilds = await _context.EngineBuilds
            .Where(b => b.Status == EngineBuildStatus.Ready)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();
    }
}

public class ServerCreateViewModel
{
    public string Name { get; set; } = "";
    public string Engine { get; set; } = nameof(ServerEngine.LlamaCpp);
    public int? Port { get; set; }

    // --- Engine configuration ---

    /// <summary>Optional managed build to bind this server to (auto-fills the folder path; llama.cpp only).</summary>
    public Guid? EngineBuildId { get; set; }
    public string? InstallFolderPath { get; set; }
    public string? CompanionAppPath { get; set; }
    public string? EnvironmentSetupCommand { get; set; }
}
