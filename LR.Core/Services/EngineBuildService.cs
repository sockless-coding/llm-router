using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Services;

/// <summary>
/// Tracks in-flight engine builds (official-release installs and, in a later phase, source
/// compiles) and runs them on background tasks. Singleton so a build survives across requests;
/// uses <see cref="IServiceScopeFactory"/> per operation to reach the scoped DbContext — the same
/// shape as <see cref="ModelDownloadService"/>.
/// </summary>
public class EngineBuildService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGitHubClient _github;
    private readonly IEngineBuildProgressPublisher _progressPublisher;
    private readonly IEngineBuildSettingsService _settings;
    private readonly ILogger<EngineBuildService> _logger;

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();

    public EngineBuildService(
        IServiceScopeFactory scopeFactory,
        IGitHubClient github,
        IEngineBuildProgressPublisher progressPublisher,
        IEngineBuildSettingsService settings,
        ILogger<EngineBuildService> logger)
    {
        _scopeFactory = scopeFactory;
        _github = github;
        _progressPublisher = progressPublisher;
        _settings = settings;
        _logger = logger;
    }

    public bool IsRunning(Guid buildId) => _active.ContainsKey(buildId);

    /// <summary>The staging folder for a build's transient files and its <c>build.log</c>.</summary>
    public async Task<string?> GetWorkRootAsync(Guid buildId)
    {
        var settings = await _settings.GetAsync();
        if (string.IsNullOrWhiteSpace(settings.InstallRootFolder) && string.IsNullOrWhiteSpace(settings.BuildWorkspaceFolder))
            return null;
        return Path.Combine(settings.ResolveWorkspaceRoot(), ".work", buildId.ToString("N"));
    }

    /// <summary>Full transcript of a build's <c>build.log</c>, or null if there isn't one.</summary>
    public async Task<string?> ReadBuildLogAsync(Guid buildId)
    {
        var workRoot = await GetWorkRootAsync(buildId);
        if (workRoot is null) return null;
        var path = Path.Combine(workRoot, "build.log");
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return await sr.ReadToEndAsync();
        }
        catch { return null; }
    }

    public bool Cancel(Guid buildId)
    {
        if (!_active.TryGetValue(buildId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    /// <summary>
    /// Creates a placeholder <see cref="LlamaCppBuild"/> row (Status = Downloading) and kicks off
    /// the official-release install pipeline on a background task. Returns the build's ID.
    /// </summary>
    public async Task<Guid> StartReleaseInstallAsync(BackendType backend, string? releaseTag, string? name)
    {
        var settings = await _settings.GetAsync();
        var installRoot = settings.InstallRootFolder;
        if (string.IsNullOrWhiteSpace(installRoot))
            throw new InvalidOperationException("Set an engine install root folder in Settings before installing builds.");
        var workspaceRoot = settings.ResolveWorkspaceRoot();

        // Resolve "latest" up front so the folder is named for the actual build number.
        if (releaseTag is null)
        {
            var latest = await _github.GetLatestReleaseAsync(EngineBuildManager.LlamaCppRepo)
                ?? throw new InvalidOperationException("Could not reach GitHub to look up the latest release.");
            releaseTag = EngineBuilds.ReleaseAssetResolver.ExtractBuildTag(latest.TagName) ?? latest.TagName;
        }

        var tagLabel = releaseTag;
        var folderName = SanitizeFolder($"{tagLabel}-{backend.ToString().ToLowerInvariant()}");
        var outputDir = Path.GetFullPath(Path.Combine(installRoot, folderName));

        Guid buildId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();

            if (await context.LlamaCppBuilds.AnyAsync(b => b.InstallPath == outputDir))
                throw new InvalidOperationException($"A build already exists at {outputDir}. Delete it first or pick a different release.");

            var build = new LlamaCppBuild
            {
                Id = Guid.NewGuid(),
                Name = name ?? $"llama.cpp {tagLabel} ({backend})",
                BackendType = backend,
                Source = EngineBuildSource.OfficialRelease,
                InstallPath = outputDir,
                VersionTag = releaseTag,
                Status = EngineBuildStatus.Downloading,
            };
            context.LlamaCppBuilds.Add(build);
            await context.SaveChangesAsync();
            buildId = build.Id;
        }

        var cts = new CancellationTokenSource();
        _active[buildId] = cts;
        _ = Task.Run(() => RunReleaseInstallAsync(buildId, backend, releaseTag, workspaceRoot, outputDir, cts.Token));
        return buildId;
    }

    /// <summary>
    /// Creates a placeholder <see cref="LlamaCppBuild"/> row (Status = Building) and kicks off the
    /// source-compile pipeline for a recipe on a background task. Returns the build's ID.
    /// </summary>
    public async Task<Guid> StartSourceBuildAsync(Guid recipeId, string? gitRefOverride, string? name)
    {
        var settings = await _settings.GetAsync();
        var installRoot = settings.InstallRootFolder;
        if (string.IsNullOrWhiteSpace(installRoot))
            throw new InvalidOperationException("Set an engine install root folder in Settings before building.");
        var workspaceRoot = settings.ResolveWorkspaceRoot();

        LlamaCppBuildRecipe recipe;
        Guid buildId;
        string outputDir;
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            recipe = await context.LlamaCppBuildRecipes.FindAsync(recipeId)
                ?? throw new InvalidOperationException("Recipe not found.");

            var refLabel = string.IsNullOrWhiteSpace(gitRefOverride) ? recipe.GitRef : gitRefOverride!;
            var recipeSlug = SanitizeFolder(recipe.Name).Replace(' ', '-').ToLowerInvariant();
            var folderName = SanitizeFolder($"{refLabel}-{recipe.BackendType.ToString().ToLowerInvariant()}-{recipeSlug}");
            outputDir = Path.GetFullPath(Path.Combine(installRoot, folderName));

            if (await context.LlamaCppBuilds.AnyAsync(b => b.InstallPath == outputDir))
                throw new InvalidOperationException($"A build already exists at {outputDir}. Delete it or change the recipe/ref.");

            var build = new LlamaCppBuild
            {
                Id = Guid.NewGuid(),
                Name = name ?? $"{recipe.Name} ({refLabel})",
                BackendType = recipe.BackendType,
                Source = EngineBuildSource.SourceCompile,
                RecipeId = recipe.Id,
                InstallPath = outputDir,
                Status = EngineBuildStatus.Building,
            };
            context.LlamaCppBuilds.Add(build);
            await context.SaveChangesAsync();
            buildId = build.Id;
        }

        var cts = new CancellationTokenSource();
        _active[buildId] = cts;
        _ = Task.Run(() => RunSourceBuildAsync(buildId, recipe, gitRefOverride, workspaceRoot, outputDir, cts.Token));
        return buildId;
    }

    /// <summary>
    /// Refreshes an existing build <em>in place</em>: the new version is staged in scratch, then
    /// swapped into the build's current <see cref="LlamaCppBuild.InstallPath"/> and the same row is
    /// updated — so every server bound to it picks up the new binaries with no re-pointing. A
    /// release build re-downloads; a source build recompiles from its recipe. Returns the build ID
    /// (unchanged). Throws if the build is busy or a bound server is still running.
    /// </summary>
    public async Task<Guid> StartUpdateAsync(Guid buildId, string? refOverride)
    {
        var settings = await _settings.GetAsync();
        var workspaceRoot = settings.ResolveWorkspaceRoot();

        EngineBuildSource source;
        BackendType backend;
        string installPath;
        LlamaCppBuildRecipe? recipe = null;

        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            var build = await context.LlamaCppBuilds.FindAsync(buildId)
                ?? throw new InvalidOperationException("Build not found.");

            if (_active.ContainsKey(buildId) ||
                build.Status is EngineBuildStatus.Downloading or EngineBuildStatus.Building or EngineBuildStatus.Pending)
                throw new InvalidOperationException("This build is already running.");

            if (string.IsNullOrWhiteSpace(build.InstallPath))
                throw new InvalidOperationException("This build has no install folder to update in place.");

            // The executable would be locked (and swapping binaries under a live inference server
            // is unsafe), so require bound servers to be stopped first.
            var busyServers = await context.BackendConfigs
                .Where(c => c.EngineBuildId == buildId)
                .Join(context.ServerInstances, c => c.ServerInstanceId, s => s.Id, (c, s) => s.Status)
                .CountAsync(st => st == ServerStatus.Running || st == ServerStatus.Starting || st == ServerStatus.Reconnecting);
            if (busyServers > 0)
                throw new InvalidOperationException(
                    $"Stop the {busyServers} running server(s) bound to this build before updating it in place.");

            if (build.Source == EngineBuildSource.SourceCompile)
            {
                recipe = build.RecipeId is { } rid ? await context.LlamaCppBuildRecipes.FindAsync(rid) : null;
                if (recipe is null)
                    throw new InvalidOperationException(
                        "The recipe this build was compiled from no longer exists — rebuild it as a new build instead.");
            }

            source = build.Source;
            backend = build.BackendType;
            installPath = build.InstallPath;

            build.Status = source == EngineBuildSource.OfficialRelease
                ? EngineBuildStatus.Downloading
                : EngineBuildStatus.Building;
            build.StatusMessage = "Updating in place…";
            await context.SaveChangesAsync();
        }

        var cts = new CancellationTokenSource();
        _active[buildId] = cts;
        _ = Task.Run(() => RunUpdateAsync(buildId, source, backend, installPath, recipe, refOverride, workspaceRoot, cts.Token));
        return buildId;
    }

    private async Task RunUpdateAsync(
        Guid buildId, EngineBuildSource source, BackendType backend, string installPath,
        LlamaCppBuildRecipe? recipe, string? refOverride, string workspaceRoot, CancellationToken ct)
    {
        var workRoot = Path.Combine(workspaceRoot, ".work", buildId.ToString("N"));
        Directory.CreateDirectory(workRoot);
        var sink = new BuildProgressSink(buildId, _progressPublisher, Path.Combine(workRoot, "build.log"), _logger);

        // Build into scratch first; the live install folder is only touched once the new version is
        // fully assembled, so a failed or cancelled update leaves the working copy exactly as it was.
        var staging = Path.Combine(workRoot, "staging");
        TryDelete(staging);
        Directory.CreateDirectory(staging);

        string? sharedSrc = null;
        if (source == EngineBuildSource.SourceCompile)
        {
            var repoKey = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(recipe!.GitRepoUrl)))[..12].ToLowerInvariant();
            sharedSrc = Path.Combine(workspaceRoot, ".src", repoKey);
        }

        var ctx = new BuildContext
        {
            BuildId = buildId,
            Repo = EngineBuildManager.LlamaCppRepo,
            BackendType = backend,
            Recipe = recipe,
            GitRefOverride = source == EngineBuildSource.SourceCompile
                ? (string.IsNullOrWhiteSpace(refOverride) ? null : refOverride)
                : null,
            ReleaseTag = source == EngineBuildSource.OfficialRelease
                ? (string.IsNullOrWhiteSpace(refOverride) ? null : refOverride)
                : null,
            WorkRoot = workRoot,
            OutputDir = staging,
            SourceDirOverride = sharedSrc,
            EnvSetupCommand = recipe?.EnvironmentSetupCommand,
            Sink = sink,
        };

        var pipeline = source == EngineBuildSource.OfficialRelease
            ? new IBuildStep[]
            {
                new ResolveReleaseAssetStep(_github),
                new DownloadArchiveStep(_github),
                new ExtractArchiveStep(),
                new FinalizeBuildStep(),
            }
            : new IBuildStep[]
            {
                new GitSyncStep(),
                new CMakeConfigureStep(),
                new CMakeBuildStep(),
                new CollectArtifactsStep(),
                new FinalizeBuildStep(),
            };

        try
        {
            await EngineBuildRunner.RunAsync(pipeline, ctx, ct);

            // ctx.OutputDir is the resolved staging folder (FinalizeBuildStep may have descended into
            // a nested folder). Mirror it onto the existing install path, in place.
            await sink.PhaseAsync("swap", $"Swapping the new version into {installPath}…");
            MirrorDirectory(ctx.OutputDir, installPath);

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            var build = await context.LlamaCppBuilds.FindAsync(new object?[] { buildId }, ct);
            if (build is not null)
            {
                build.Status = EngineBuildStatus.Ready;
                build.StatusMessage = null;
                // InstallPath is deliberately left as-is — that's the whole point of an in-place update.
                build.VersionTag = ctx.ResolvedVersionTag ?? build.VersionTag;
                build.CommitSha = ctx.ResolvedCommitSha ?? build.CommitSha;
                build.TargetOs = ctx.TargetOs ?? build.TargetOs;
                build.TargetArch = ctx.TargetArch ?? build.TargetArch;
                build.SizeBytes = TryGetDirectorySize(installPath);
                build.BuildCompletedAt = DateTime.UtcNow;
                if (source == EngineBuildSource.OfficialRelease && ctx.ResolvedVersionTag is not null)
                    build.Name = $"llama.cpp {ctx.ResolvedVersionTag} ({backend})";
                await context.SaveChangesAsync(ct);
            }

            await sink.CompletedAsync($"Updated in place to {ctx.ResolvedVersionTag ?? "the latest version"}.");
            TryDelete(staging);
            TryDelete(Path.Combine(workRoot, "download"));
            TryDelete(Path.Combine(workRoot, "extract"));
            TryDelete(Path.Combine(workRoot, "build"));
        }
        catch (OperationCanceledException)
        {
            await RestoreReadyAfterFailedUpdateAsync(buildId, "Update cancelled — the existing install was left untouched.");
            await sink.ErrorAsync("Cancelled");
            TryDelete(staging);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Engine build {BuildId} in-place update failed.", buildId);
            await RestoreReadyAfterFailedUpdateAsync(buildId, $"Last update attempt failed (existing install kept): {ex.Message}");
            await sink.ErrorAsync(ex.Message);
            TryDelete(staging);
        }
        finally
        {
            _active.TryRemove(buildId, out _);
        }
    }

    /// <summary>
    /// A failed in-place update leaves the on-disk install working, so the row goes back to
    /// <see cref="EngineBuildStatus.Ready"/> (not <see cref="EngineBuildStatus.Error"/>, which would
    /// make bound servers stop resolving it) with the failure recorded in the status message.
    /// </summary>
    private async Task RestoreReadyAfterFailedUpdateAsync(Guid buildId, string message)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var build = await context.LlamaCppBuilds.FindAsync(buildId);
        if (build is null) return;
        build.Status = EngineBuildStatus.Ready;
        build.StatusMessage = message;
        await context.SaveChangesAsync();
    }

    private async Task RunSourceBuildAsync(
        Guid buildId, LlamaCppBuildRecipe recipe, string? gitRefOverride, string workspaceRoot, string outputDir, CancellationToken ct)
    {
        var workRoot = Path.Combine(workspaceRoot, ".work", buildId.ToString("N"));
        Directory.CreateDirectory(workRoot);
        var sink = new BuildProgressSink(buildId, _progressPublisher, Path.Combine(workRoot, "build.log"), _logger);

        // Shared, reused git checkout keyed by repo URL — lives in the workspace so it's shared
        // across every recipe that builds from the same repo.
        var repoKey = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(recipe.GitRepoUrl)))[..12].ToLowerInvariant();
        var sharedSrc = Path.Combine(workspaceRoot, ".src", repoKey);

        var ctx = new BuildContext
        {
            BuildId = buildId,
            Repo = EngineBuildManager.LlamaCppRepo,
            BackendType = recipe.BackendType,
            Recipe = recipe,
            GitRefOverride = gitRefOverride,
            WorkRoot = workRoot,
            OutputDir = outputDir,
            SourceDirOverride = sharedSrc,
            EnvSetupCommand = recipe.EnvironmentSetupCommand,
            Sink = sink,
        };

        var pipeline = new IBuildStep[]
        {
            new GitSyncStep(),
            new CMakeConfigureStep(),
            new CMakeBuildStep(),
            new CollectArtifactsStep(),
            new FinalizeBuildStep(),
        };

        try
        {
            await EngineBuildRunner.RunAsync(pipeline, ctx, ct);

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            var build = await context.LlamaCppBuilds.FindAsync(new object?[] { buildId }, ct);
            if (build is not null)
            {
                build.Status = EngineBuildStatus.Ready;
                build.StatusMessage = null;
                build.InstallPath = ctx.OutputDir;
                build.VersionTag = ctx.ResolvedVersionTag ?? build.VersionTag;
                build.CommitSha = ctx.ResolvedCommitSha ?? build.CommitSha;
                build.TargetOs = ReleaseAssetResolver.DetectHost().Os;
                build.TargetArch = ReleaseAssetResolver.DetectHost().Arch;
                build.SizeBytes = TryGetDirectorySize(ctx.OutputDir);
                build.BuildCompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(ct);
            }

            await sink.CompletedAsync($"Built to {ctx.OutputDir}.");
            TryDelete(Path.Combine(workRoot, "build"));
        }
        catch (OperationCanceledException)
        {
            await MarkErrorAsync(buildId, "Build cancelled.");
            await sink.ErrorAsync("Cancelled");
            TryDelete(ctx.OutputDir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Source build {BuildId} failed.", buildId);
            await MarkErrorAsync(buildId, ex.Message);
            await sink.ErrorAsync(ex.Message);
        }
        finally
        {
            _active.TryRemove(buildId, out _);
        }
    }

    private async Task RunReleaseInstallAsync(
        Guid buildId, BackendType backend, string? releaseTag, string workspaceRoot, string outputDir, CancellationToken ct)
    {
        var workRoot = Path.Combine(workspaceRoot, ".work", buildId.ToString("N"));
        Directory.CreateDirectory(workRoot);
        var sink = new BuildProgressSink(buildId, _progressPublisher, Path.Combine(workRoot, "build.log"), _logger);

        var ctx = new BuildContext
        {
            BuildId = buildId,
            Repo = EngineBuildManager.LlamaCppRepo,
            BackendType = backend,
            ReleaseTag = releaseTag,
            WorkRoot = workRoot,
            OutputDir = outputDir,
            Sink = sink,
        };

        var pipeline = new IBuildStep[]
        {
            new ResolveReleaseAssetStep(_github),
            new DownloadArchiveStep(_github),
            new ExtractArchiveStep(),
            new FinalizeBuildStep(),
        };

        try
        {
            await EngineBuildRunner.RunAsync(pipeline, ctx, ct);

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            var build = await context.LlamaCppBuilds.FindAsync(new object?[] { buildId }, ct);
            if (build is not null)
            {
                build.Status = EngineBuildStatus.Ready;
                build.StatusMessage = null;
                build.InstallPath = ctx.OutputDir;
                build.VersionTag = ctx.ResolvedVersionTag ?? build.VersionTag;
                build.CommitSha = ctx.ResolvedCommitSha ?? build.CommitSha;
                build.TargetOs = ctx.TargetOs;
                build.TargetArch = ctx.TargetArch;
                build.SizeBytes = TryGetDirectorySize(ctx.OutputDir);
                build.BuildCompletedAt = DateTime.UtcNow;
                if (ctx.ResolvedVersionTag is not null)
                    build.Name = $"llama.cpp {ctx.ResolvedVersionTag} ({backend})";
                await context.SaveChangesAsync(ct);
            }

            await sink.CompletedAsync($"Installed to {ctx.OutputDir}.");
            // Keep build.log (and the cached git clone, for source builds) — just drop the bulky
            // download/extraction scratch.
            TryDelete(Path.Combine(workRoot, "download"));
            TryDelete(Path.Combine(workRoot, "extract"));
        }
        catch (OperationCanceledException)
        {
            await MarkErrorAsync(buildId, "Install cancelled.");
            await sink.ErrorAsync("Cancelled");
            TryDelete(ctx.OutputDir);
            TryDelete(workRoot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Engine build {BuildId} failed.", buildId);
            await MarkErrorAsync(buildId, ex.Message);
            await sink.ErrorAsync(ex.Message);
        }
        finally
        {
            _active.TryRemove(buildId, out _);
        }
    }

    private async Task MarkErrorAsync(Guid buildId, string message)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var build = await context.LlamaCppBuilds.FindAsync(buildId);
        if (build is null) return;
        build.Status = EngineBuildStatus.Error;
        build.StatusMessage = message;
        await context.SaveChangesAsync();
    }

    private static string SanitizeFolder(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return name;
    }

    private static long? TryGetDirectorySize(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch { return null; }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Makes <paramref name="dest"/> match <paramref name="source"/>: every file is copied over
    /// (overwriting), then anything left in <paramref name="dest"/> that isn't in
    /// <paramref name="source"/> is removed — so a renamed or dropped binary from the previous
    /// version doesn't linger. The folder itself (the path servers bind to) is kept.
    /// </summary>
    private static void MirrorDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);

        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        foreach (var file in Directory.GetFiles(dest, "*", SearchOption.AllDirectories))
        {
            if (!File.Exists(Path.Combine(source, Path.GetRelativePath(dest, file))))
                File.Delete(file);
        }

        // Deepest-first so a pruned parent is already empty by the time we reach it.
        foreach (var dir in Directory.GetDirectories(dest, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            if (!Directory.Exists(Path.Combine(source, Path.GetRelativePath(dest, dir))) &&
                !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }
}
