using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LR.Application.Pages.Features.Servers;

public partial class EditModel : PageModel
{
    private readonly IServerManager _serverManager;
    private readonly LRDbContext _context;
    private readonly IEngineCatalog _engines;
    private readonly IServerConcurrencyLimiter _limiter;

    public List<EngineBuild> AvailableBuilds { get; set; } = new();
    public List<MemoryGroup> ExistingGroups { get; set; } = new();
    public List<(string Name, long Mb, bool Overridden)> PresetEstimates { get; set; } = new();

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

    public EditModel(IServerManager serverManager, LRDbContext context, IEngineCatalog engines, IServerConcurrencyLimiter limiter)
    {
        _limiter = limiter;
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
        ViewModel.MemoryGroup = Server.MemoryGroup;
        ViewModel.IdleUnloadMinutes = Server.IdleUnloadMinutes;
        var group = Server.MemoryGroup is null ? null : await _context.MemoryGroups.FindAsync(Server.MemoryGroup);
        ViewModel.GroupBudgetGb = group is { BudgetMb: > 0 } ? Math.Round(group.BudgetMb / 1024.0, 1) : null;

        AvailableBuilds = await LoadAvailableBuildsAsync(Server.Engine);
        await LoadMemoryInfoAsync();

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
        await LoadMemoryInfoAsync();

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
        await SaveMemorySettingsAsync();

        // Reload server with updated config
        Server = await _context.ServerInstances
            .Include(s => s.Config)
            .FirstOrDefaultAsync(s => s.Id == Id);

        return Page();
    }
}

public partial class EditModel
{
    private async Task LoadMemoryInfoAsync()
    {
        ExistingGroups = await _context.MemoryGroups.OrderBy(g => g.Name).ToListAsync();

        var scheduler = new MemoryGroupScheduler(_context, _limiter);
        PresetEstimates.Clear();
        foreach (var preset in await _context.ModelPresets.Where(p => p.ServerInstanceId == Id).OrderBy(p => p.Name).ToListAsync())
            PresetEstimates.Add((preset.Name, await scheduler.EstimateAsync(preset) / (1024 * 1024), preset.MemoryEstimateMb is > 0));
    }

    /// <summary>
    /// Saves the server's group and idle settings and upserts the group's shared budget. Groups
    /// no server references any more are dropped so the suggestion list stays clean.
    /// </summary>
    private async Task SaveMemorySettingsAsync()
    {
        var server = await _context.ServerInstances.FindAsync(Id);
        if (server is null)
            return;

        var groupName = string.IsNullOrWhiteSpace(ViewModel.MemoryGroup) ? null : ViewModel.MemoryGroup.Trim();
        server.MemoryGroup = groupName;
        server.IdleUnloadMinutes = ViewModel.IdleUnloadMinutes is > 0 ? ViewModel.IdleUnloadMinutes : null;

        if (groupName is not null)
        {
            var group = await _context.MemoryGroups.FindAsync(groupName);
            if (group is null)
                _context.MemoryGroups.Add(group = new MemoryGroup { Name = groupName });
            group.BudgetMb = ViewModel.GroupBudgetGb is > 0 ? (int)Math.Round(ViewModel.GroupBudgetGb.Value * 1024) : 0;
        }

        await _context.SaveChangesAsync();

        var referenced = await _context.ServerInstances.Where(s => s.MemoryGroup != null).Select(s => s.MemoryGroup!).Distinct().ToListAsync();
        _context.MemoryGroups.RemoveRange(await _context.MemoryGroups.Where(g => !referenced.Contains(g.Name)).ToListAsync());
        await _context.SaveChangesAsync();
    }

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

    [System.ComponentModel.DataAnnotations.MaxLength(64)]
    public string? MemoryGroup { get; set; }

    [System.ComponentModel.DataAnnotations.Range(0, 4096)]
    public double? GroupBudgetGb { get; set; }

    [System.ComponentModel.DataAnnotations.Range(0, 100000)]
    public int? IdleUnloadMinutes { get; set; }
}
