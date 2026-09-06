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
    /// Available server engines for the dropdown.
    /// </summary>
    public List<EngineOption> Engines { get; }

    /// <summary>
    /// Ready managed llama.cpp builds the new server can be bound to.
    /// </summary>
    public List<LlamaCppBuild> AvailableBuilds { get; set; } = new();

    public ServerCreateModel(IServerManager serverManager, LRDbContext context)
    {
        _serverManager = serverManager;
        _context = context;
        Engines = Enum.GetValues<ServerEngine>().Select(e => new EngineOption
        {
            Value = e.ToString(),
            Label = e switch
            {
                ServerEngine.LlamaCpp => "llama.cpp",
                ServerEngine.Ollama => "Ollama",
                _ => e.ToString()
            }
        }).ToList();
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

        var engine = Enum.Parse<ServerEngine>(ViewModel.Engine, ignoreCase: true);

        LlamaCppBuild? boundBuild = null;
        if (engine == ServerEngine.LlamaCpp && ViewModel.EngineBuildId is { } buildId)
        {
            boundBuild = await _context.LlamaCppBuilds.FindAsync(buildId);
            if (boundBuild is null)
            {
                ModelState.AddModelError(nameof(ViewModel.EngineBuildId), "The selected build no longer exists.");
                return Page();
            }
        }

        // A managed build supplies the folder path; otherwise a manually-entered one is required.
        if (engine == ServerEngine.LlamaCpp && boundBuild is null)
        {
            if (string.IsNullOrWhiteSpace(ViewModel.LlamaCppExecutableFolderPath))
            {
                ModelState.AddModelError(nameof(ViewModel.LlamaCppExecutableFolderPath), "Select a managed build or enter a folder path for llama.cpp.");
                return Page();
            }

            if (!Directory.Exists(ViewModel.LlamaCppExecutableFolderPath))
            {
                ModelState.AddModelError(nameof(ViewModel.LlamaCppExecutableFolderPath), $"The folder '{ViewModel.LlamaCppExecutableFolderPath}' does not exist.");
                return Page();
            }
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

        await _serverManager.CreateInstanceAsync(ViewModel.Name, engine, configData, ViewModel.Port);

        TempData["SuccessMessage"] = $"Server \"{ViewModel.Name}\" created successfully.";
        return RedirectToPage("Index");
    }

    private async Task LoadAvailableBuildsAsync()
    {
        AvailableBuilds = await _context.LlamaCppBuilds
            .Where(b => b.Status == EngineBuildStatus.Ready)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();
    }
}

public class ServerCreateViewModel
{
    public string Name { get; set; } = "";
    public string Engine { get; set; } = "LlamaCpp";
    public int? Port { get; set; }

    // --- llama.cpp-specific configuration ---

    /// <summary>Optional managed build to bind this server to (auto-fills the folder path).</summary>
    public Guid? EngineBuildId { get; set; }
    public string? LlamaCppExecutableFolderPath { get; set; }
    public string? CompanionAppPath { get; set; }
    public string? EnvironmentSetupCommand { get; set; }
}

public class EngineOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}
