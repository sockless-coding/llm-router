using System.Formats.Tar;
using System.IO.Compression;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// Resolves the GitHub release (latest, or the pinned tag) and picks the archive(s) matching the
/// requested backend and host OS/arch.
/// </summary>
public sealed class ResolveReleaseAssetStep : IBuildStep
{
    private readonly IGitHubClient _github;
    public string Phase => "resolve";

    public ResolveReleaseAssetStep(IGitHubClient github) => _github = github;

    public async Task ExecuteAsync(BuildContext ctx, CancellationToken ct)
    {
        var (os, arch) = ReleaseAssetResolver.DetectHost();
        ctx.TargetOs = os;
        ctx.TargetArch = arch;

        await ctx.Sink.PhaseAsync(Phase, ctx.ReleaseTag is null
            ? "Looking up the latest llama.cpp release…"
            : $"Looking up release {ctx.ReleaseTag}…");

        var release = ctx.ReleaseTag is null
            ? await _github.GetLatestReleaseAsync(ctx.Repo, ct)
            : await _github.GetReleaseByTagAsync(ctx.Repo, ctx.ReleaseTag, ct);

        if (release is null)
            throw new InvalidOperationException(ctx.ReleaseTag is null
                ? "Could not reach GitHub to look up the latest release."
                : $"Release '{ctx.ReleaseTag}' was not found.");

        ctx.Release = release;
        ctx.ResolvedVersionTag = ReleaseAssetResolver.ExtractBuildTag(release.TagName) ?? release.TagName;

        var selected = ReleaseAssetResolver.Resolve(release.Assets, ctx.BackendType, os, arch);
        ctx.SelectedAssets.AddRange(selected);

        await ctx.Sink.PhaseAsync(Phase,
            $"Selected {string.Join(" + ", ctx.SelectedAssets.Select(a => a.Name))} from {release.TagName}.");
    }
}

/// <summary>Downloads every selected asset into the work root.</summary>
public sealed class DownloadArchiveStep : IBuildStep
{
    private readonly IGitHubClient _github;
    public string Phase => "download";

    public DownloadArchiveStep(IGitHubClient github) => _github = github;

    public async Task ExecuteAsync(BuildContext ctx, CancellationToken ct)
    {
        var downloadDir = Path.Combine(ctx.WorkRoot, "download");
        Directory.CreateDirectory(downloadDir);

        foreach (var asset in ctx.SelectedAssets)
        {
            ct.ThrowIfCancellationRequested();
            await ctx.Sink.PhaseAsync(Phase, $"Downloading {asset.Name} ({asset.Size / 1024 / 1024} MB)…");
            var dest = Path.Combine(downloadDir, asset.Name);
            await _github.DownloadAssetAsync(asset.BrowserDownloadUrl, dest, ctx.Sink.AsProgress(), ct);
        }
    }
}

/// <summary>Extracts every downloaded archive on top of each other into the output folder.</summary>
public sealed class ExtractArchiveStep : IBuildStep
{
    public string Phase => "extract";

    public Task ExecuteAsync(BuildContext ctx, CancellationToken ct)
    {
        var downloadDir = Path.Combine(ctx.WorkRoot, "download");
        Directory.CreateDirectory(ctx.OutputDir);

        foreach (var asset in ctx.SelectedAssets)
        {
            ct.ThrowIfCancellationRequested();
            var archivePath = Path.Combine(downloadDir, asset.Name);
            var stagingDir = Path.Combine(ctx.WorkRoot, "extract", Path.GetFileNameWithoutExtension(asset.Name));
            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
            Directory.CreateDirectory(stagingDir);

            if (asset.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                asset.Name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = File.OpenRead(archivePath);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gz, stagingDir, overwriteFiles: true);
            }
            else
            {
                ZipFile.ExtractToDirectory(archivePath, stagingDir, overwriteFiles: true);
            }

            // llama.cpp archives sometimes nest everything under a single "build/bin" or top-level
            // folder — flatten so llama-server ends up directly in OutputDir.
            var root = FindPayloadRoot(stagingDir);
            CopyDirectory(root, ctx.OutputDir);
        }

        return ctx.Sink.PhaseAsync(Phase, $"Extracted to {ctx.OutputDir}.");
    }

    private static string FindPayloadRoot(string dir)
    {
        var current = dir;
        while (true)
        {
            var entries = Directory.GetFileSystemEntries(current);
            var files = entries.Where(File.Exists).ToArray();
            var dirs = entries.Where(Directory.Exists).ToArray();
            // Descend through wrapper folders that contain nothing but a single subfolder.
            if (files.Length == 0 && dirs.Length == 1)
            {
                current = dirs[0];
                continue;
            }
            // If there's a "build/bin" layout, that's where the binaries are.
            var buildBin = Path.Combine(current, "build", "bin");
            if (Directory.Exists(buildBin))
                return buildBin;
            return current;
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
}

/// <summary>
/// Verifies <c>llama-server</c> landed in the output folder and records the size. The runner reads
/// <see cref="BuildContext.ResolvedVersionTag"/>/<see cref="BuildContext.ResolvedCommitSha"/> back
/// onto the build row.
/// </summary>
public sealed class FinalizeBuildStep : IBuildStep
{
    public string Phase => "finalize";

    public Task ExecuteAsync(BuildContext ctx, CancellationToken ct)
    {
        var serverName = OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
        var serverPath = Directory.EnumerateFiles(ctx.OutputDir, serverName, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException($"{serverName} was not found under {ctx.OutputDir} after install.");

        // If it's in a subfolder, point the install path at that folder.
        var actualFolder = Path.GetDirectoryName(serverPath)!;
        if (!string.Equals(actualFolder, ctx.OutputDir, StringComparison.OrdinalIgnoreCase))
            ctx.OutputDir = actualFolder;

        return ctx.Sink.PhaseAsync(Phase, $"Ready: {serverPath}");
    }
}
