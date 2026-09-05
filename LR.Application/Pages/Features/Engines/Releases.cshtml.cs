using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Application.Pages.Features.Engines;

public class EngineReleasesModel : PageModel
{
    private readonly IGitHubClient _github;
    private readonly IEngineBuildManager _manager;
    private readonly IEngineBuildSettingsService _settings;

    public IReadOnlyList<GitHubRelease> Releases { get; set; } = new List<GitHubRelease>();
    public string HostOs { get; private set; } = "";
    public string HostArch { get; private set; } = "";
    public bool RootConfigured { get; private set; }
    public string? LoadError { get; set; }

    public EngineReleasesModel(IGitHubClient github, IEngineBuildManager manager, IEngineBuildSettingsService settings)
    {
        _github = github;
        _manager = manager;
        _settings = settings;
    }

    public async Task OnGetAsync()
    {
        (HostOs, HostArch) = ReleaseAssetResolver.DetectHost();
        RootConfigured = !string.IsNullOrWhiteSpace((await _settings.GetAsync()).BuildsRootFolder);

        try
        {
            var all = await _github.ListReleasesAsync(_manager.Repo, 20);
            // Skip non-build tags (e.g. the "nightly-tag" marker release) — keep the b#### builds.
            Releases = all
                .Where(r => System.Text.RegularExpressions.Regex.IsMatch(r.TagName, @"^b\d{3,}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .Take(15)
                .ToList();
        }
        catch (Exception ex)
        {
            LoadError = $"Could not load releases from GitHub: {ex.Message}";
        }
    }

    /// <summary>The backends that have a prebuilt archive in the given release for this host.</summary>
    public IEnumerable<BackendType> AvailableBackends(GitHubRelease release)
    {
        foreach (var backend in new[] { BackendType.Cpu, BackendType.Cuda, BackendType.Vulkan, BackendType.Sycl })
        {
            if (ReleaseAssetResolver.IsAvailable(release.Assets, backend, HostOs, HostArch))
                yield return backend;
        }
    }
}
