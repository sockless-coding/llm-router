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

    /// <summary>Enabled remote agents the server can be placed on (besides this machine).</summary>
    public List<RemoteAgent> Agents { get; set; } = new();

    public ServerCreateModel(IServerManager serverManager, LRDbContext context, IEngineCatalog engines)
    {
        _serverManager = serverManager;
        _context = context;
        Engines = engines.All;
    }

    public async Task OnGetAsync(Guid? agentId)
    {
        ViewModel.AgentId = agentId;
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

        RemoteAgent? agent = null;
        if (ViewModel.AgentId is { } agentId)
        {
            agent = Agents.FirstOrDefault(a => a.Id == agentId);
            if (agent is null)
            {
                ModelState.AddModelError(nameof(ViewModel.AgentId), "That agent no longer exists or is disabled.");
                return Page();
            }
            if (!descriptor.SupportsRemoteAgents)
            {
                ModelState.AddModelError(nameof(ViewModel.Engine), $"{descriptor.DisplayName} servers can't run on remote agents yet.");
                return Page();
            }
        }

        EngineBuild? boundBuild = null;
        if (descriptor.SupportsManagedBuilds && ViewModel.EngineBuildId is { } buildId)
        {
            boundBuild = await _context.EngineBuilds.FindAsync(buildId);
            if (boundBuild is null || boundBuild.Engine != engine || boundBuild.AgentId != ViewModel.AgentId)
            {
                ModelState.AddModelError(nameof(ViewModel.EngineBuildId), boundBuild is null
                    ? "The selected build no longer exists."
                    : boundBuild.Engine != engine
                        ? $"That install isn't a {descriptor.DisplayName} install."
                        : "That install is on a different machine than the server.");
                return Page();
            }
        }

        // A managed build supplies the folder path; otherwise a manually-entered one is required.
        if (boundBuild is null)
        {
            if (string.IsNullOrWhiteSpace(ViewModel.InstallFolderPath))
            {
                ModelState.AddModelError($"{nameof(ViewModel)}.{nameof(ViewModel.InstallFolderPath)}", descriptor.SupportsManagedBuilds
                    ? $"Select a managed build or enter a folder path for {descriptor.DisplayName}."
                    : $"Enter the folder path for {descriptor.DisplayName}.");
                return Page();
            }

            // A folder on a remote agent can't be checked from here — its wrapper reports a bad path on start.
            if (agent is null && descriptor.ValidateInstallFolder(ViewModel.InstallFolderPath) is { } folderError)
            {
                ModelState.AddModelError($"{nameof(ViewModel)}.{nameof(ViewModel.InstallFolderPath)}", folderError);
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
            AgentId = agent?.Id,
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
        Agents = await _context.RemoteAgents
            .Where(a => a.IsEnabled)
            .OrderBy(a => a.Name)
            .ToListAsync();
    }
}

public class ServerCreateViewModel
{
    public string Name { get; set; } = "";
    public string Engine { get; set; } = nameof(ServerEngine.LlamaCpp);

    /// <summary>The remote agent to run the server on; null = this machine.</summary>
    public Guid? AgentId { get; set; }
    public int? Port { get; set; }

    // --- Engine configuration ---

    /// <summary>Optional managed build to bind this server to (auto-fills the folder path; llama.cpp only).</summary>
    public Guid? EngineBuildId { get; set; }
    public string? InstallFolderPath { get; set; }
    public string? CompanionAppPath { get; set; }
    public string? EnvironmentSetupCommand { get; set; }
}
