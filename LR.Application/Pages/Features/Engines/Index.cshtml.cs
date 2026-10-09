using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Core.Services.EngineBuilds;

namespace LR.Application.Pages.Features.Engines;

/// <summary>
/// The Engines page: shared build settings, then one tab per registered engine
/// (<see cref="IEngineCatalog"/>). Each tab's content is the partial <c>Tabs/_&lt;Engine&gt;.cshtml</c>,
/// rendered with this model — adding an engine's version management means adding that partial.
/// </summary>
public class EnginesIndexModel : PageModel
{
    private readonly IEngineBuildManager _manager;
    private readonly IEngineBuildSettingsService _settings;
    private readonly EngineBuildService _buildService;

    /// <summary>The engines shown as tabs.</summary>
    public IReadOnlyList<IEngineDescriptor> Engines { get; }

    /// <summary>The tab being shown (<c>?engine=</c>), defaulting to the first registered engine.</summary>
    [BindProperty(SupportsGet = true)]
    public ServerEngine? Engine { get; set; }

    public IEngineDescriptor SelectedEngine => Engines.FirstOrDefault(e => e.Engine == Engine) ?? Engines[0];

    /// <summary>Installs of the selected engine.</summary>
    public IReadOnlyList<EngineBuild> Builds { get; set; } = new List<EngineBuild>();
    public IReadOnlyList<LlamaCppBuildRecipe> Recipes { get; set; } = new List<LlamaCppBuildRecipe>();
    public Dictionary<Guid, int> ServerUsageCounts { get; set; } = new();

    /// <summary>Strata tab: what each checkout holds on disk (engine version, installed models), read live.</summary>
    public Dictionary<Guid, StrataCheckoutInfo> StrataInfo { get; set; } = new();

    [BindProperty]
    public string InstallRootFolder { get; set; } = string.Empty;

    [BindProperty]
    public string? BuildWorkspaceFolder { get; set; }

    [BindProperty]
    public string? GitHubApiToken { get; set; }

    public string? StatusMessage { get; set; }

    public EnginesIndexModel(IEngineBuildManager manager, IEngineBuildSettingsService settings, EngineBuildService buildService, IEngineCatalog engines)
    {
        _manager = manager;
        _settings = settings;
        _buildService = buildService;
        Engines = engines.All;
    }

    public async Task OnGetAsync()
    {
        var settings = await _settings.GetAsync();
        InstallRootFolder = settings.InstallRootFolder;
        BuildWorkspaceFolder = settings.BuildWorkspaceFolder;
        GitHubApiToken = settings.GitHubApiToken;
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostSaveSettingsAsync()
    {
        try
        {
            await _settings.SaveAsync(InstallRootFolder ?? string.Empty, BuildWorkspaceFolder, GitHubApiToken);
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
        var engine = SelectedEngine.Engine;
        Builds = await _manager.GetBuildsAsync(engine);
        if (engine == ServerEngine.LlamaCpp)
            Recipes = await _manager.GetRecipesAsync();

        foreach (var b in Builds)
        {
            ServerUsageCounts[b.Id] = await _manager.GetServerUsageCountAsync(b.Id);
            if (b.Engine == ServerEngine.Strata && b.Status is not EngineBuildStatus.Missing)
                StrataInfo[b.Id] = new StrataCheckoutInfo(
                    StrataLayout.ReadEngineVersion(b.InstallPath),
                    StrataLayout.FindRunConfigs(b.InstallPath),
                    StrataLayout.IsGitClone(b.InstallPath));
        }
    }
}

/// <param name="EngineVersion">The installed engine binary's version (can lag the source version).</param>
/// <param name="RunConfigs">The models set up in the checkout.</param>
/// <param name="IsGitClone">Whether the router can pull updates into it.</param>
public sealed record StrataCheckoutInfo(string? EngineVersion, IReadOnlyList<StrataRunConfig> RunConfigs, bool IsGitClone);
