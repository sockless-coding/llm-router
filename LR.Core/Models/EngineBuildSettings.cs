using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// Configuration for the engine-build area — where managed llama.cpp builds live on disk and an
/// optional GitHub token for higher API rate limits / private forks. Persisted as a single-row
/// table (edited from Settings), following the same pattern as <see cref="ModelLibrarySettings"/>.
/// </summary>
[Table("EngineBuildSettings")]
public class EngineBuildSettings
{
    /// <summary>Singleton row — this table only ever holds one record, with Id fixed at 1.</summary>
    [Key]
    public int Id { get; set; } = 1;

    /// <summary>
    /// Root folder under which each build gets its own versioned subfolder, and where the shared
    /// git clone (<c>src/</c>) and build logs are cached.
    /// </summary>
    [MaxLength(1024)]
    public string BuildsRootFolder { get; set; } = string.Empty;

    /// <summary>
    /// Optional GitHub API token (bearer). Raises the anonymous rate limit for release/compare
    /// lookups and allows private forks as a build source.
    /// </summary>
    [MaxLength(256)]
    public string? GitHubApiToken { get; set; }
}
