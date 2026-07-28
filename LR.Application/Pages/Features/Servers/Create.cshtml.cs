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

    /// <summary>
    /// Available server engines for the dropdown.
    /// </summary>
    public List<EngineOption> Engines { get; }

    public ServerCreateModel(IServerManager serverManager)
    {
        _serverManager = serverManager;
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

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var engine = Enum.Parse<ServerEngine>(ViewModel.Engine, ignoreCase: true);

        // Validate llama.cpp-specific fields
        if (engine == ServerEngine.LlamaCpp && string.IsNullOrWhiteSpace(ViewModel.LlamaCppExecutableFolderPath))
        {
            ModelState.AddModelError(nameof(ViewModel.LlamaCppExecutableFolderPath), "Folder path is required for llama.cpp.");
            return Page();
        }

        if (engine == ServerEngine.LlamaCpp && !Directory.Exists(ViewModel.LlamaCppExecutableFolderPath!))
        {
            ModelState.AddModelError(nameof(ViewModel.LlamaCppExecutableFolderPath), $"The folder '{ViewModel.LlamaCppExecutableFolderPath}' does not exist.");
            return Page();
        }

        var configData = new BackendConfigData
        {
            LlamaCppExecutableFolderPath = ViewModel.LlamaCppExecutableFolderPath,
            CompanionAppPath = string.IsNullOrWhiteSpace(ViewModel.CompanionAppPath) ? null : ViewModel.CompanionAppPath,
        };

        await _serverManager.CreateInstanceAsync(ViewModel.Name, engine, configData, ViewModel.Port);

        return RedirectToPage("Index");
    }
}

public class ServerCreateViewModel
{
    public string Name { get; set; } = "";
    public string Engine { get; set; } = "LlamaCpp";
    public int? Port { get; set; }

    // --- llama.cpp-specific configuration ---
    public string? LlamaCppExecutableFolderPath { get; set; }
    public string? CompanionAppPath { get; set; }
}

public class EngineOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}

