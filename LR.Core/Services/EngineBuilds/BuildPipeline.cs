using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// One stage of an engine-build pipeline. Steps are ordered, independently testable, and share
/// state through <see cref="BuildContext"/> — a release install and a source compile are just
/// different ordered lists of these.
/// </summary>
public interface IBuildStep
{
    /// <summary>Short phase name, surfaced in progress events (e.g. "download", "cmake-build").</summary>
    string Phase { get; }

    Task ExecuteAsync(BuildContext ctx, CancellationToken ct);
}

/// <summary>
/// Mutable state threaded through every <see cref="IBuildStep"/> in a pipeline run. Early steps
/// resolve the release/commit and fill in the version fields; later steps place files and the
/// runner reads the resolved values back onto the <see cref="LlamaCppBuild"/> row.
/// </summary>
public sealed class BuildContext
{
    public required Guid BuildId { get; init; }
    public required string Repo { get; init; }
    public required BackendType BackendType { get; init; }

    /// <summary>Release tag to install; null means "latest". Release pipeline only.</summary>
    public string? ReleaseTag { get; set; }

    /// <summary>Recipe driving a source compile. Source pipeline only.</summary>
    public LlamaCppBuildRecipe? Recipe { get; init; }

    /// <summary>Per-run git ref override for a source compile (falls back to the recipe's ref).</summary>
    public string? GitRefOverride { get; init; }

    /// <summary>Staging area: downloads, archive extraction, the cached git clone, and build.log.</summary>
    public required string WorkRoot { get; init; }

    /// <summary>Final versioned folder the finished build is placed in.</summary>
    public required string OutputDir { get; set; }

    /// <summary>
    /// Cached git checkout. Defaults under the work root; the service points source builds at a
    /// shared location so the clone is reused across builds of the same repo.
    /// </summary>
    public string? SourceDirOverride { get; init; }

    public string SourceDir => SourceDirOverride ?? Path.Combine(WorkRoot, "src");
    public string BuildDir => Path.Combine(WorkRoot, "build");

    public string? EnvSetupCommand { get; init; }

    public required BuildProgressSink Sink { get; init; }

    // --- filled in by steps ---
    public GitHubRelease? Release { get; set; }
    public List<GitHubReleaseAsset> SelectedAssets { get; } = new();
    public string? ResolvedVersionTag { get; set; }
    public string? ResolvedCommitSha { get; set; }
    public string? TargetOs { get; set; }
    public string? TargetArch { get; set; }
}

/// <summary>
/// Fans build progress out to two places: the SignalR publisher (live UI) and a
/// <c>build.log</c> file under the work root (so the Detail page can show the full transcript
/// after the fact). Thread-safe for concurrent line writes from stdout/stderr pumps.
/// </summary>
public sealed class BuildProgressSink
{
    private readonly Guid _buildId;
    private readonly IEngineBuildProgressPublisher _publisher;
    private readonly string _logFilePath;
    private readonly ILogger _logger;
    private readonly object _fileLock = new();

    public BuildProgressSink(Guid buildId, IEngineBuildProgressPublisher publisher, string logFilePath, ILogger logger)
    {
        _buildId = buildId;
        _publisher = publisher;
        _logFilePath = logFilePath;
        _logger = logger;
        Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
    }

    public string LogFilePath => _logFilePath;

    public Task PhaseAsync(string phase, string? message = null, int? percent = null)
    {
        AppendToFile($"== [{phase}] {message}");
        return _publisher.PublishAsync(new EngineBuildProgress
        {
            BuildId = _buildId,
            Phase = phase,
            Message = message,
            Percent = percent,
            Status = "running",
        });
    }

    public Task LineAsync(string phase, string line)
    {
        AppendToFile(line);
        return _publisher.PublishAsync(new EngineBuildProgress
        {
            BuildId = _buildId,
            Phase = phase,
            LogLine = line,
            Status = "running",
        });
    }

    public IProgress<EngineBuildProgress> AsProgress() => new Progress<EngineBuildProgress>(p =>
    {
        p.BuildId = _buildId;
        if (p.LogLine is not null) AppendToFile(p.LogLine);
        _ = _publisher.PublishAsync(p);
    });

    public Task CompletedAsync(string message) =>
        _publisher.PublishAsync(new EngineBuildProgress { BuildId = _buildId, Phase = "finalize", Message = message, Percent = 100, Status = "completed" });

    public Task ErrorAsync(string message)
    {
        AppendToFile($"!! ERROR: {message}");
        return _publisher.PublishAsync(new EngineBuildProgress { BuildId = _buildId, Phase = "error", Status = "error", ErrorMessage = message });
    }

    private void AppendToFile(string line)
    {
        try
        {
            lock (_fileLock)
                File.AppendAllText(_logFilePath, $"{DateTime.UtcNow:HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to append to engine build log {Path}", _logFilePath);
        }
    }
}
