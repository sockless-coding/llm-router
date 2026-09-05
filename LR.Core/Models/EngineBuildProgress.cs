namespace LR.Core.Models;

/// <summary>
/// Live progress for an in-flight engine build (download or compile), broadcast over SignalR.
/// Mirrors <see cref="DownloadProgress"/>.
/// </summary>
public class EngineBuildProgress
{
    public Guid BuildId { get; set; }

    /// <summary>
    /// Current pipeline phase: <c>resolve</c>, <c>download</c>, <c>extract</c>, <c>git</c>,
    /// <c>cmake-configure</c>, <c>cmake-build</c>, <c>collect</c>, <c>finalize</c>.
    /// </summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>0-100 when a percentage is meaningful (downloads); null for open-ended steps.</summary>
    public int? Percent { get; set; }

    /// <summary>Human-readable status line for the current phase.</summary>
    public string? Message { get; set; }

    /// <summary>A single line of raw tool output (git/cmake), when this event carries one.</summary>
    public string? LogLine { get; set; }

    /// <summary><c>running</c> | <c>completed</c> | <c>error</c>.</summary>
    public string Status { get; set; } = "running";

    public string? ErrorMessage { get; set; }
}

/// <summary>
/// The result of a "check for updates" call on an installed build: how far behind the latest
/// llama.cpp release it is, and the commits/PRs in between.
/// </summary>
public class EngineBuildUpdateStatus
{
    public Guid BuildId { get; set; }

    /// <summary>The installed build's tag/SHA that the comparison used as its base.</summary>
    public string? InstalledRef { get; set; }

    /// <summary>The latest release tag the comparison used as its head.</summary>
    public string? LatestTag { get; set; }

    public bool UpdateAvailable { get; set; }

    public int AheadBy { get; set; }
    public int BehindBy { get; set; }

    /// <summary>Link to the full GitHub compare view.</summary>
    public string? CompareUrl { get; set; }

    public List<EngineBuildChangelogEntry> Commits { get; set; } = new();

    /// <summary>Set when the check could not complete (offline, rate-limited, unknown ref).</summary>
    public string? Error { get; set; }
}

/// <summary>One row in the changelog list between an installed build and the latest release.</summary>
public class EngineBuildChangelogEntry
{
    public string Sha { get; set; } = string.Empty;
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
    public string Summary { get; set; } = string.Empty;
    public string? Author { get; set; }
    public DateTimeOffset? Date { get; set; }
    public string? CommitUrl { get; set; }

    /// <summary>Pull-request number parsed from a trailing <c>(#1234)</c> in the commit summary, if any.</summary>
    public int? PullRequestNumber { get; set; }
    public string? PullRequestUrl { get; set; }
}
