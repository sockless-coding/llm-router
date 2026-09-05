using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Application.Pages.Features.Engines;

public class EnginesIndexModel : PageModel
{
    private readonly IEngineBuildManager _manager;
    private readonly IEngineBuildSettingsService _settings;
    private readonly EngineBuildService _buildService;

    public IReadOnlyList<LlamaCppBuild> Builds { get; set; } = new List<LlamaCppBuild>();
    public IReadOnlyList<LlamaCppBuildRecipe> Recipes { get; set; } = new List<LlamaCppBuildRecipe>();
    public Dictionary<Guid, int> ServerUsageCounts { get; set; } = new();

    [BindProperty]
    public string BuildsRootFolder { get; set; } = string.Empty;

    [BindProperty]
    public string? GitHubApiToken { get; set; }

    public string? StatusMessage { get; set; }

    public EnginesIndexModel(IEngineBuildManager manager, IEngineBuildSettingsService settings, EngineBuildService buildService)
    {
        _manager = manager;
        _settings = settings;
        _buildService = buildService;
    }

    public async Task OnGetAsync()
    {
        var settings = await _settings.GetAsync();
        BuildsRootFolder = settings.BuildsRootFolder;
        GitHubApiToken = settings.GitHubApiToken;
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostSaveSettingsAsync()
    {
        try
        {
            await _settings.SaveAsync(BuildsRootFolder ?? string.Empty, GitHubApiToken);
            StatusMessage = "Engine build settings saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save settings: {ex.Message}";
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Builds = await _manager.GetAllBuildsAsync();
        Recipes = await _manager.GetRecipesAsync();
        foreach (var b in Builds)
            ServerUsageCounts[b.Id] = await _manager.GetServerUsageCountAsync(b.Id);
    }
}
