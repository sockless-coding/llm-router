using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Data;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Services;

/// <summary>
/// TensorFold for an Intel GPU on Windows: an image built in Docker Desktop (<see cref="TensorFoldDocker"/>).
/// This is the build-row bookkeeping around it, as for vLLM's Intel image.
/// </summary>
public partial class EngineBuildService
{
    /// <summary>
    /// Builds the TensorFold XPU image at <paramref name="gitRef"/> (null = the newest engine release) for
    /// <paramref name="device"/> on a background task. Returns the new row's ID.
    /// </summary>
    public async Task<Guid> StartTensorFoldDockerInstallAsync(string? gitRef, string? device, string? name)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("The TensorFold XPU Docker image is for Windows. On Linux, compile the XPU recipe instead.");
        var settings = await _settings.GetAsync();
        if (string.IsNullOrWhiteSpace(settings.InstallRootFolder))
            throw new InvalidOperationException("Set an engine install root folder (Engine Build Settings) before installing.");

        var tag = await ResolveTensorFoldRefAsync(gitRef);
        var xpuDevice = string.IsNullOrWhiteSpace(device) ? TensorFoldDocker.DefaultDevice : device.Trim().ToLowerInvariant();
        var installPath = Path.GetFullPath(Path.Combine(settings.InstallRootFolder, TensorFoldDocker.FolderName(SanitizeFolder(tag))));

        // The image tag is shared, so one Docker install at a time; a failed install removes its folder.
        Guid buildId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            var existing = await context.EngineBuilds
                .Where(b => b.AgentId == null && b.Engine == ServerEngine.TensorFold && b.BackendType == BackendType.Xpu)
                .Select(b => b.InstallPath).ToListAsync();
            if (existing.FirstOrDefault(TensorFoldDocker.IsDockerFolder) is { } other)
                throw new InvalidOperationException($"There's already a TensorFold XPU Docker install ({other}). Update it in place instead.");
            if (Directory.Exists(installPath) && Directory.EnumerateFileSystemEntries(installPath).Any())
                throw new InvalidOperationException($"'{installPath}' already exists. Delete it first.");

            var build = new EngineBuild
            {
                Name = string.IsNullOrWhiteSpace(name) ? TensorFoldDockerBuildName(tag) : name.Trim(),
                Engine = ServerEngine.TensorFold,
                Source = EngineBuildSource.OfficialRelease,
                BackendType = BackendType.Xpu,
                InstallPath = installPath,
                VersionTag = tag,
                TargetOs = "ubuntu",
                TargetArch = "x64",
                Status = EngineBuildStatus.Building,
                StatusMessage = "Building the image…",
            };
            context.EngineBuilds.Add(build);
            await context.SaveChangesAsync();
            buildId = build.Id;
        }

        var workspaceRoot = WorkspaceRoot(settings);
        StartJob(buildId, ct => RunTensorFoldDockerJobAsync(buildId, installPath, tag, xpuDevice, workspaceRoot, isNewInstall: true, ct));
        return buildId;
    }

    /// <summary>The in-place update behind <see cref="StartUpdateAsync"/>: the same image tag, built again at another ref.</summary>
    private async Task StartTensorFoldDockerUpdateAsync(LRDbContext context, EngineBuild build, string? refOverride, string workspaceRoot)
    {
        var tag = await ResolveTensorFoldRefAsync(refOverride);
        build.Status = EngineBuildStatus.Building;
        build.StatusMessage = "Rebuilding the image…";
        await context.SaveChangesAsync();

        var buildId = build.Id;
        var installPath = build.InstallPath;
        StartJob(buildId, ct => RunTensorFoldDockerJobAsync(buildId, installPath, tag, TensorFoldDocker.DefaultDevice, workspaceRoot, isNewInstall: false, ct));
    }

    public static string TensorFoldDockerBuildName(string tag) => $"TensorFold {tag} (Intel XPU, Docker)";

    /// <summary>The ref to build: the one given, else the newest engine release (never the desktop app's).</summary>
    private async Task<string> ResolveTensorFoldRefAsync(string? gitRef)
    {
        if (!string.IsNullOrWhiteSpace(gitRef))
        {
            var trimmed = gitRef.Trim();
            if (!TensorFoldDocker.IsValidRef(trimmed))
                throw new InvalidOperationException($"'{trimmed}' isn't a TensorFold tag or branch.");
            return trimmed;
        }
        var latest = await TensorFoldReleaseProfile.Instance.GetLatestReleaseAsync(_github)
            ?? throw new InvalidOperationException("Could not reach GitHub to look up the newest TensorFold release. Enter a tag or branch instead.");
        return latest.TagName;
    }

    private async Task RunTensorFoldDockerJobAsync(Guid buildId, string installPath, string gitRef, string device, string workspaceRoot, bool isNewInstall, CancellationToken ct)
    {
        var workRoot = Path.Combine(workspaceRoot, ".work", buildId.ToString("N"));
        Directory.CreateDirectory(workRoot);
        var sink = new BuildProgressSink(buildId, _progressPublisher, Path.Combine(workRoot, "build.log"), _logger);

        try
        {
            var installed = await TensorFoldDocker.InstallAsync(installPath, gitRef, device, sink, ct);

            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
                var build = await context.EngineBuilds.FindAsync([buildId], ct)
                    ?? throw new InvalidOperationException("The install was removed while the job ran.");
                if (build.VersionTag is { } old && build.Name == TensorFoldDockerBuildName(old))
                    build.Name = TensorFoldDockerBuildName(installed.VersionTag);
                build.VersionTag = installed.VersionTag;
                build.SizeBytes = installed.SizeBytes;
                build.Status = EngineBuildStatus.Ready;
                build.StatusMessage = null;
                build.BuildCompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(ct);
            }

            await sink.CompletedAsync($"TensorFold {installed.VersionTag} for Intel XPU is ready (image {TensorFoldDocker.ImageTag}).");
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "Cancelled." : ex.Message;
            if (ex is not OperationCanceledException)
                _logger.LogError(ex, "TensorFold Docker job for {BuildId} failed.", buildId);

            if (isNewInstall)
            {
                try { await TensorFoldDocker.DeleteAsync(installPath); }
                catch (Exception cleanup) { _logger.LogWarning(cleanup, "Could not clean up the failed TensorFold install at {Path}", installPath); }
                await MarkErrorAsync(buildId, message);
            }
            else
            {
                // Docker only replaces the tag once the new image is built, so the old one still serves.
                await RestoreReadyAfterFailedUpdateAsync(buildId, $"Last update attempt failed (existing image kept): {message}");
            }
            await sink.ErrorAsync(message);
        }
        finally
        {
            _active.TryRemove(buildId, out _);
        }
    }
}
