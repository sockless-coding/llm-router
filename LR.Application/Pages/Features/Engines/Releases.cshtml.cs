using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Core.Services.EngineBuilds;

namespace LR.Application.Pages.Features.Engines;

/// <summary>
/// Lists an engine's GitHub releases to install (<c>?engine=</c>): the prebuilt archives per backend of
/// an engine with an <see cref="EngineReleaseProfile"/> (llama.cpp, stable-diffusion.cpp), or Strata's
/// releases (whose setup picks the right ready-made engine for this PC).
/// </summary>
public partial class EngineReleasesModel : PageModel
{
    private readonly IGitHubClient _github;
    private readonly IEngineBuildManager _manager;
    private readonly IEngineBuildSettingsService _settings;
    private readonly EngineBuildService _buildService;

    [BindProperty(SupportsGet = true)]
    public ServerEngine Engine { get; set; } = ServerEngine.LlamaCpp;

    /// <summary>The prebuilt-release engine being listed (null for Strata).</summary>
    public EngineReleaseProfile? Profile { get; private set; }

    public IReadOnlyList<GitHubRelease> Releases { get; set; } = new List<GitHubRelease>();
    public string HostOs { get; private set; } = "";
    public string HostArch { get; private set; } = "";
    public bool RootConfigured { get; private set; }
    public string? InstallRoot { get; private set; }
    public string? LoadError { get; set; }

    /// <summary>Strata: release tags already installed as a tracked release install.</summary>
    public HashSet<string> InstalledTags { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Strata: the tools an install needs, and whether this PC has them.</summary>
    public IReadOnlyList<StrataPrerequisite> Prerequisites { get; private set; } = [];

    public EngineReleasesModel(IGitHubClient github, IEngineBuildManager manager, IEngineBuildSettingsService settings, EngineBuildService buildService)
    {
        _github = github;
        _manager = manager;
        _settings = settings;
        _buildService = buildService;
    }

    public async Task OnGetAsync()
    {
        (HostOs, HostArch) = ReleaseAssetResolver.DetectHost();
        InstallRoot = (await _settings.GetAsync()).InstallRootFolder;
        RootConfigured = !string.IsNullOrWhiteSpace(InstallRoot);

        try
        {
            if (Engine == ServerEngine.Strata)
            {
                var all = await _github.ListReleasesAsync(StrataLayout.Repo, 20);
                Releases = all.Where(r => StrataLayout.IsReleaseTag(r.TagName)).Take(15).ToList();
                foreach (var b in await _manager.GetBuildsAsync(ServerEngine.Strata))
                    if (b.Source == EngineBuildSource.OfficialRelease && b.VersionTag is { } tag)
                        InstalledTags.Add(tag);
            }
            else
            {
                Profile = EngineReleaseProfile.For(Engine) ?? LlamaCppReleaseProfile.Instance;
                Engine = Profile.Engine;
                var all = await _github.ListReleasesAsync(Profile.Repo, 20);
                // Skip non-build tags (e.g. llama.cpp's "nightly-tag" marker release).
                Releases = all
                    .Where(r => Profile.IsInstallableRelease(r.TagName))
                    .Take(15)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            LoadError = $"Could not load releases from GitHub: {ex.Message}";
        }

        if (Engine == ServerEngine.Strata)
        {
            // Without a ready-made engine for this OS (Linux), Strata compiles one, so it needs a C++ toolchain.
            var hasReadyMadeEngine = Releases.Count > 0
                ? Releases.Any(r => r.Assets.Any(a => StrataLayout.VariantOfAsset(a.Name) is not null))
                : OperatingSystem.IsWindows();
            Prerequisites = await _buildService.CheckStrataPrerequisitesAsync(compilesEngine: !hasReadyMadeEngine, HttpContext.RequestAborted);
        }
    }

    /// <summary>The backends that have a prebuilt archive in the given release for this host.</summary>
    public IEnumerable<BackendType> AvailableBackends(GitHubRelease release)
    {
        if (Profile is null)
            yield break;
        foreach (var backend in Profile.CandidateBackends)
        {
            if (Profile.IsAvailable(release.Assets, backend, HostOs, HostArch))
                yield return backend;
        }
    }

    /// <summary>A Strata release's headline: the first paragraph of its notes, without markdown emphasis.</summary>
    public static string? Headline(GitHubRelease release)
    {
        var first = release.Body?
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .FirstOrDefault(p => p.Length > 0 && !p.StartsWith('#'));
        return first is null ? null : MarkdownEmphasis().Replace(first, "");
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB" : $"{bytes / (double)(1L << 20):F0} MB";

    [GeneratedRegex(@"\*\*|__|`")]
    private static partial Regex MarkdownEmphasis();
}
