using LR.Core.Models;

namespace LR.Core.Services.AppUpdates;

/// <summary>
/// Version and asset conventions of the router's own GitHub releases (see
/// <c>.github/workflows/release.yml</c>): tags are <c>yyyy.m.n</c> (optionally <c>v</c>-prefixed)
/// and each release carries <c>llm-router-&lt;version&gt;-win-x64.zip</c> (framework-dependent) and
/// <c>llm-router-&lt;version&gt;-win-x64-self-contained.zip</c>.
/// </summary>
public static class AppRelease
{
    public const string Repository = "sockless-coding/llm-router";

    private const string FrameworkDependentSuffix = "-win-x64.zip";
    private const string SelfContainedSuffix = "-win-x64-self-contained.zip";

    /// <summary>
    /// Parses a release tag or assembly informational version (<c>2026.10.0</c>, <c>v2026.10.0</c>,
    /// <c>2026.10.0+abc123</c>) into a <see cref="Version"/> with build and revision filled in, so
    /// <c>2026.10</c> and <c>2026.10.0</c> compare equal.
    /// </summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];
        var end = trimmed.IndexOfAny(['+', '-']);
        if (end >= 0)
            trimmed = trimmed[..end];

        if (!Version.TryParse(trimmed, out var parsed))
            return false;

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        return true;
    }

    /// <summary>Formats a version the way release tags are written (<c>2026.10.0</c>).</summary>
    public static string Format(Version version) =>
        version.Revision > 0 ? version.ToString(4) : version.ToString(3);

    /// <summary>
    /// The newest published release (no drafts, no prereleases) with a parseable version, or null.
    /// </summary>
    public static (GitHubRelease Release, Version Version)? PickLatest(IEnumerable<GitHubRelease> releases)
    {
        (GitHubRelease Release, Version Version)? best = null;
        foreach (var release in releases)
        {
            if (release.Draft || release.Prerelease || !TryParseVersion(release.TagName, out var version))
                continue;
            if (best is null || version > best.Value.Version)
                best = (release, version);
        }
        return best;
    }

    /// <summary>
    /// The archive matching the running install's flavor — a self-contained install must stay
    /// self-contained, since a framework-dependent build would need a runtime that may not be there.
    /// </summary>
    public static GitHubReleaseAsset? SelectAsset(GitHubRelease release, bool selfContained) =>
        release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith("llm-router-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(selfContained ? SelfContainedSuffix : FrameworkDependentSuffix, StringComparison.OrdinalIgnoreCase));
}
