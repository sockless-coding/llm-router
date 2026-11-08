using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Data;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Services;

/// <summary>
/// vLLM installs on this machine: a self-contained venv per version and backend, in WSL on Windows. The
/// steps themselves are in <see cref="VllmInstaller"/>, which agents run too; this is the build-row
/// bookkeeping around them.
/// </summary>
public partial class EngineBuildService
{
    /// <summary>
    /// Installs vLLM <paramref name="version"/> (null = newest release) on a background task. Fails
    /// straight away if WSL can't be used. Returns the new row's ID.
    /// </summary>
    public async Task<Guid> StartVllmInstallAsync(BackendType backend, string? version, string? wslDistro, string? name)
    {
        var settings = await _settings.GetAsync();
        if (!OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(settings.InstallRootFolder))
            throw new InvalidOperationException("Set an engine install root folder (Engine Build Settings) before installing vLLM.");
        if (!VllmLayout.InstallBackends(OperatingSystem.IsWindows()).Contains(backend))
            throw new InvalidOperationException($"vLLM can't be installed for {backend} on this machine.");

        var tag = await ResolveVllmTagAsync(version);
        var folderName = $"vllm-{tag}-{backend.ToString().ToLowerInvariant()}";
        var outputDir = OperatingSystem.IsWindows() ? folderName : Path.GetFullPath(Path.Combine(settings.InstallRootFolder, folderName));
        var request = new VllmInstallRequest(backend, tag, outputDir, string.IsNullOrWhiteSpace(wslDistro) ? null : wslDistro.Trim());
        var installPath = await VllmInstaller.ResolveInstallPathAsync(request, CancellationToken.None);

        // A failed install removes its folder, so it must never start in a folder holding anything else.
        if (Directory.Exists(installPath) && Directory.EnumerateFileSystemEntries(installPath).Any())
            throw new InvalidOperationException($"'{installPath}' already exists. Delete it, or point a server at it directly.");

        Guid buildId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            if (await context.EngineBuilds.AnyAsync(b => b.AgentId == null && b.InstallPath == installPath))
                throw new InvalidOperationException($"'{installPath}' is already tracked. Remove it first, or update it instead.");

            var build = new EngineBuild
            {
                Name = string.IsNullOrWhiteSpace(name) ? VllmBuildName(tag, backend) : name.Trim(),
                Engine = ServerEngine.Vllm,
                Source = EngineBuildSource.OfficialRelease,
                BackendType = backend,
                InstallPath = installPath,
                VersionTag = tag,
                Status = EngineBuildStatus.Downloading,
                StatusMessage = "Installing…",
            };
            context.EngineBuilds.Add(build);
            await context.SaveChangesAsync();
            buildId = build.Id;
        }

        var workspaceRoot = WorkspaceRoot(settings);
        StartJob(buildId, ct => RunVllmJobAsync(buildId, request with { OutputDir = installPath }, workspaceRoot, isNewInstall: true, ct));
        return buildId;
    }

    /// <summary>The in-place update behind <see cref="StartUpdateAsync"/>: the same venv, moved to another vLLM version.</summary>
    private async Task StartVllmUpdateAsync(LRDbContext context, EngineBuild build, string? refOverride, string workspaceRoot)
    {
        var tag = await ResolveVllmTagAsync(refOverride);
        build.Status = EngineBuildStatus.Downloading;
        build.StatusMessage = "Updating in place…";
        await context.SaveChangesAsync();

        var request = new VllmInstallRequest(build.BackendType, tag, build.InstallPath);
        var buildId = build.Id;
        StartJob(buildId, ct => RunVllmJobAsync(buildId, request, workspaceRoot, isNewInstall: false, ct));
    }

    public static string VllmBuildName(string tag, BackendType backend) => $"vLLM {tag} ({backend})";

    /// <summary>The tag of <paramref name="version"/>, or of the newest version on PyPI.</summary>
    private static async Task<string> ResolveVllmTagAsync(string? version)
    {
        if (!string.IsNullOrWhiteSpace(version))
            return "v" + VllmLayout.NormalizeVersion(version);

        return await VllmReleases.GetLatestTagAsync()
            ?? throw new InvalidOperationException("Could not reach PyPI to look up the newest vLLM. Enter a version to install instead.");
    }

    private async Task RunVllmJobAsync(Guid buildId, VllmInstallRequest request, string workspaceRoot, bool isNewInstall, CancellationToken ct)
    {
        var workRoot = Path.Combine(workspaceRoot, ".work", buildId.ToString("N"));
        Directory.CreateDirectory(workRoot);
        var sink = new BuildProgressSink(buildId, _progressPublisher, Path.Combine(workRoot, "build.log"), _logger);

        try
        {
            var installed = await VllmInstaller.InstallAsync(request, sink, ct);

            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
                var build = await context.EngineBuilds.FindAsync([buildId], ct)
                    ?? throw new InvalidOperationException("The install was removed while the job ran.");
                if (build.VersionTag is { } old && build.Name == VllmBuildName(old, build.BackendType))
                    build.Name = VllmBuildName(installed.VersionTag, build.BackendType);
                build.InstallPath = installed.InstallPath;
                build.VersionTag = installed.VersionTag;
                build.SizeBytes = installed.SizeBytes;
                build.TargetOs = installed.TargetOs;
                build.TargetArch = installed.TargetArch;
                build.Status = EngineBuildStatus.Ready;
                build.StatusMessage = null;
                build.BuildCompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(ct);
            }

            await sink.CompletedAsync($"vLLM {installed.VersionTag} is ready in {installed.InstallPath}.");
        }
        catch (OperationCanceledException)
        {
            await SettleFailedVllmJobAsync(buildId, request.OutputDir, isNewInstall, "Cancelled.");
            await sink.ErrorAsync("Cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "vLLM job for {BuildId} failed.", buildId);
            await SettleFailedVllmJobAsync(buildId, request.OutputDir, isNewInstall, ex.Message);
            await sink.ErrorAsync(ex.Message);
        }
        finally
        {
            _active.TryRemove(buildId, out _);
        }
    }

    /// <summary>
    /// A failed new install is removed so it can simply be retried. A failed update stays Ready with the
    /// failure noted: pip only replaces packages once it has resolved and downloaded them all.
    /// </summary>
    private async Task SettleFailedVllmJobAsync(Guid buildId, string installPath, bool isNewInstall, string message)
    {
        if (isNewInstall)
        {
            try { await VllmInstaller.DeleteAsync(installPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not clean up the failed vLLM install at {Path}", installPath); }
            await MarkErrorAsync(buildId, message);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        if (await context.EngineBuilds.FindAsync(buildId) is not { } build) return;
        build.Status = EngineBuildStatus.Ready;
        build.StatusMessage = $"Last update attempt failed: {message}";
        await context.SaveChangesAsync();
    }
}
