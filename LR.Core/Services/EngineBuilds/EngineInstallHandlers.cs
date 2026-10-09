using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>llama.cpp builds: a folder holding <c>llama-server</c>, compared against the latest release.</summary>
public sealed class LlamaCppInstallHandler : IEngineInstallHandler
{
    public ServerEngine Engine => ServerEngine.LlamaCpp;

    public string Repo => EngineBuildManager.LlamaCppRepo;

    public bool IsInstallPresent(string installPath) =>
        File.Exists(Path.Combine(installPath, "llama-server.exe")) ||
        File.Exists(Path.Combine(installPath, "llama-server"));

    public bool IsReady(string installPath) => IsInstallPresent(installPath);

    public async Task<string?> GetLatestRefAsync(EngineBuild build, IGitHubClient github, CancellationToken ct = default) =>
        (await github.GetLatestReleaseAsync(Repo, ct))?.TagName;

    public void RefreshFromDisk(EngineBuild build) { }
}

/// <summary>
/// Strata checkouts: a release install (<see cref="EngineBuildSource.OfficialRelease"/>, pinned to a
/// tag) is compared against the latest release, a checkout following the branch
/// (<see cref="EngineBuildSource.GitCheckout"/>) against the branch Strata's own updater pulls. Only
/// ready once Strata's setup has run there (its <c>.venv</c> and a model config exist).
/// </summary>
public sealed class StrataInstallHandler : IEngineInstallHandler
{
    public ServerEngine Engine => ServerEngine.Strata;

    public string Repo => StrataLayout.Repo;

    public bool IsInstallPresent(string installPath) => StrataLayout.IsCheckout(installPath);

    public bool IsReady(string installPath) => StrataLayout.IsSetUp(installPath);

    public async Task<string?> GetLatestRefAsync(EngineBuild build, IGitHubClient github, CancellationToken ct = default) =>
        build.Source == EngineBuildSource.OfficialRelease
            ? (await github.GetLatestReleaseAsync(Repo, ct))?.TagName
            : StrataLayout.Branch;

    public void RefreshFromDisk(EngineBuild build)
    {
        // A release install's version is the tag it's checked out at, set when it was installed/updated.
        if (build.Source != EngineBuildSource.OfficialRelease && StrataLayout.ReadSourceVersion(build.InstallPath) is { } version)
            build.VersionTag = "v" + version;
        build.BackendType = StrataInstallInfo.DetectBackend(build.InstallPath);
    }
}

/// <summary>Helpers for reading a Strata checkout into an <see cref="EngineBuild"/> row.</summary>
public static class StrataInstallInfo
{
    /// <summary>AMD (HIP) if any installed model runs on Strata's HIP backend, else CUDA — Strata's default.</summary>
    public static BackendType DetectBackend(string checkout) =>
        StrataLayout.FindRunConfigs(checkout).Any(c => string.Equals(c.Backend, "hip", StringComparison.OrdinalIgnoreCase))
            ? BackendType.Hip
            : BackendType.Cuda;
}
