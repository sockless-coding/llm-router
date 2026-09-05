using System.Text.Json.Serialization;

namespace LR.Core.Models;

/// <summary>
/// A published GitHub release (subset of https://docs.github.com/rest/releases/releases).
/// </summary>
public class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAsset> Assets { get; set; } = new();
}

/// <summary>A downloadable file attached to a <see cref="GitHubRelease"/>.</summary>
public class GitHubReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>
/// Result of a GitHub "compare two commits" call
/// (<c>GET /repos/{repo}/compare/{base}...{head}</c>).
/// </summary>
public class GitHubCompareResult
{
    [JsonPropertyName("status")]
    public string? Status { get; set; } // "ahead" | "behind" | "identical" | "diverged"

    [JsonPropertyName("ahead_by")]
    public int AheadBy { get; set; }

    [JsonPropertyName("behind_by")]
    public int BehindBy { get; set; }

    [JsonPropertyName("total_commits")]
    public int TotalCommits { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("commits")]
    public List<GitHubCommit> Commits { get; set; } = new();
}

/// <summary>A single commit entry from a compare/list response.</summary>
public class GitHubCommit
{
    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("commit")]
    public GitHubCommitDetail Commit { get; set; } = new();

    [JsonPropertyName("author")]
    public GitHubUser? Author { get; set; }
}

public class GitHubCommitDetail
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public GitHubCommitAuthor? Author { get; set; }

    /// <summary>First line of the commit message — what the changelog list shows.</summary>
    [JsonIgnore]
    public string Summary =>
        Message.Split('\n', 2, StringSplitOptions.TrimEntries)[0];
}

public class GitHubCommitAuthor
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("date")]
    public DateTimeOffset? Date { get; set; }
}

public class GitHubUser
{
    [JsonPropertyName("login")]
    public string? Login { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}
