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
    /// Root folder under which each finished build (compiled or downloaded) gets its own versioned
    /// subfolder. This is the path servers bind to, so it should be somewhere stable.
    /// </summary>
    [MaxLength(1024)]
    public string InstallRootFolder { get; set; } = string.Empty;

    /// <summary>
    /// Shared scratch area for building: per-build staging (<c>.work/</c>), the reused git clones
    /// (<c>.src/</c>), and build logs all live here. Reused across every recipe, so it can point at
    /// a fast/scratch disk. Optional — when blank, a <c>.build</c> folder under
    /// <see cref="InstallRootFolder"/> is used.
    /// </summary>
    [MaxLength(1024)]
    public string BuildWorkspaceFolder { get; set; } = string.Empty;

    /// <summary>
    /// The effective build workspace: <see cref="BuildWorkspaceFolder"/> when set, otherwise a
    /// <c>.build</c> subfolder of <see cref="InstallRootFolder"/>.
    /// </summary>
    public string ResolveWorkspaceRoot() =>
        !string.IsNullOrWhiteSpace(BuildWorkspaceFolder)
            ? BuildWorkspaceFolder
            : Path.Combine(InstallRootFolder, ".build");

    /// <summary>
    /// Optional GitHub API token (bearer). Raises the anonymous rate limit for release/compare
    /// lookups and allows private forks as a build source.
    /// </summary>
    [MaxLength(256)]
    public string? GitHubApiToken { get; set; }
}
