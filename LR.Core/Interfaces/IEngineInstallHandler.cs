using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Engine-specific knowledge the engine-build registry needs about an install on disk: where its
/// upstream lives (for update checks and changelogs), how to tell a usable install from a missing
/// or half-set-up one, and what can be re-read from its files. One per engine that has managed
/// installs on the Engines page; the install/update pipelines themselves live in
/// <see cref="Services.EngineBuildService"/>.
/// </summary>
public interface IEngineInstallHandler
{
    ServerEngine Engine { get; }

    /// <summary>GitHub repo ("owner/name") installs are compared against.</summary>
    string Repo { get; }

    /// <summary>True if <paramref name="installPath"/> still holds this engine's files.</summary>
    bool IsInstallPresent(string installPath);

    /// <summary>True if the install is complete enough for a server to run from it.</summary>
    bool IsReady(string installPath);

    /// <summary>
    /// The upstream ref <paramref name="build"/> is compared against when checking for updates
    /// (e.g. the latest release tag, or a branch), or null if it couldn't be determined.
    /// </summary>
    Task<string?> GetLatestRefAsync(EngineBuild build, IGitHubClient github, CancellationToken ct = default);

    /// <summary>
    /// Updates fields that can be read straight from the install's files (e.g. its version) —
    /// called during reconciliation. No-op for engines whose files don't record anything useful.
    /// </summary>
    void RefreshFromDisk(EngineBuild build);
}
