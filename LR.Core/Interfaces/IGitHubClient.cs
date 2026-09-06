using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Thin client over the public GitHub REST API — release lookup, commit comparison, and asset
/// download. Used to discover new llama.cpp builds and render the changelog between an installed
/// build and the latest release.
/// </summary>
public interface IGitHubClient
{
    /// <summary>Gets the most recent non-draft release for <paramref name="repo"/> (e.g. "ggml-org/llama.cpp").</summary>
    Task<GitHubRelease?> GetLatestReleaseAsync(string repo, CancellationToken ct = default);

    /// <summary>Gets a specific release by tag, or null if it doesn't exist.</summary>
    Task<GitHubRelease?> GetReleaseByTagAsync(string repo, string tag, CancellationToken ct = default);

    /// <summary>Lists recent releases, newest first.</summary>
    Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string repo, int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Compares two refs (tags or SHAs): <c>GET /repos/{repo}/compare/{base}...{head}</c>. Returns
    /// null if either ref is unknown.
    /// </summary>
    Task<GitHubCompareResult?> CompareAsync(string repo, string baseRef, string headRef, CancellationToken ct = default);

    /// <summary>
    /// Reads a text file from the repo at a given ref (branch/tag/SHA). Returns null if it's not
    /// found. Used to surface llama.cpp's own build docs / example scripts in the recipe editor.
    /// </summary>
    Task<string?> GetRawFileAsync(string repo, string reference, string path, CancellationToken ct = default);

    /// <summary>
    /// Streams a release asset to <paramref name="destinationPath"/>, reporting byte progress via
    /// <paramref name="progress"/> (BuildId is filled in by the caller).
    /// </summary>
    Task DownloadAssetAsync(
        string downloadUrl,
        string destinationPath,
        IProgress<EngineBuildProgress>? progress,
        CancellationToken ct = default);
}
