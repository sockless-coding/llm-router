using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// How a <see cref="EngineBuild"/> was obtained.
/// </summary>
public enum EngineBuildSource
{
    /// <summary>Prebuilt archive downloaded from the ggml-org/llama.cpp GitHub releases.</summary>
    OfficialRelease = 0,

    /// <summary>Compiled locally from source via a <see cref="LlamaCppBuildRecipe"/>.</summary>
    SourceCompile = 1,

    /// <summary>
    /// A git checkout of the engine's own repository, updated in place by pulling and re-running the
    /// engine's own setup (Strata). Its folder can hold user data (models), so it is never deleted
    /// by the router.
    /// </summary>
    GitCheckout = 2,
}

/// <summary>
/// Lifecycle status of a managed engine build.
/// </summary>
public enum EngineBuildStatus
{
    Pending = 0,
    Downloading = 1,
    Building = 2,
    Ready = 3,
    Error = 4,

    /// <summary>The install folder was expected on disk but is no longer there (found during reconciliation).</summary>
    Missing = 5,

    /// <summary>
    /// The files are there but the engine's own first-time setup hasn't been run yet (e.g. a fresh
    /// Strata clone without its <c>.venv</c>). Not bindable until it's <see cref="Ready"/>.
    /// </summary>
    NeedsSetup = 6,
}

/// <summary>
/// A tracked, versioned engine install on disk — for llama.cpp a downloaded official release or a
/// locally compiled build, for Strata a git checkout. Servers can bind to a build (via
/// <see cref="BackendConfig.EngineBuildId"/>) so the router knows exactly which version/commit each
/// install folder holds and can offer update/changelog information. Modelled on
/// <see cref="LocalModel"/> in the model library.
/// </summary>
[Table("EngineBuilds")]
public class EngineBuild
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The engine this is an install of. Only servers of the same engine can bind to it.</summary>
    public ServerEngine Engine { get; set; } = ServerEngine.LlamaCpp;

    /// <summary>The GPU compute backend this build targets.</summary>
    public BackendType BackendType { get; set; }

    /// <summary>Whether this build was downloaded or compiled.</summary>
    public EngineBuildSource Source { get; set; }

    /// <summary>
    /// For <see cref="EngineBuildSource.SourceCompile"/> builds — the recipe used. Nulled out
    /// (rather than cascade-deleted) if the recipe is later removed.
    /// </summary>
    public Guid? RecipeId { get; set; }

    /// <summary>
    /// Absolute path to the install folder — for llama.cpp the folder containing
    /// <c>llama-server</c>/<c>llama-server.exe</c>, for Strata the checkout. This is what a bound server's <see cref="BackendConfig.InstallFolderPath"/> resolves to.
    /// </summary>
    [MaxLength(1024)]
    public string InstallPath { get; set; } = string.Empty;

    /// <summary>Version when known: llama.cpp's release/build tag (<c>b####</c>), Strata's <c>v&lt;version&gt;</c>.</summary>
    [MaxLength(64)]
    public string? VersionTag { get; set; }

    /// <summary>Full 40-hex git commit SHA the build was produced from, when known.</summary>
    [MaxLength(64)]
    public string? CommitSha { get; set; }

    /// <summary>Target OS moniker (<c>win</c>/<c>ubuntu</c>/<c>macos</c>) for cross-platform bookkeeping.</summary>
    [MaxLength(32)]
    public string? TargetOs { get; set; }

    /// <summary>Target architecture (<c>x64</c>/<c>arm64</c>).</summary>
    [MaxLength(32)]
    public string? TargetArch { get; set; }

    public EngineBuildStatus Status { get; set; } = EngineBuildStatus.Pending;

    [MaxLength(2048)]
    public string? StatusMessage { get; set; }

    /// <summary>Total size of the install folder in bytes, computed after a successful install/build.</summary>
    public long? SizeBytes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the download/compile finished successfully.</summary>
    public DateTime? BuildCompletedAt { get; set; }

    /// <summary>Navigation: the recipe this build was compiled with, if any.</summary>
    [ForeignKey(nameof(RecipeId))]
    public LlamaCppBuildRecipe? Recipe { get; set; }
}
