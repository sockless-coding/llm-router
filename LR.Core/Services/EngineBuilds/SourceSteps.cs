using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// Clones (or fetches) the recipe's repo into the cached <see cref="BuildContext.SourceDir"/> and
/// checks out the requested ref, then records the resolved commit SHA and build tag.
/// </summary>
public sealed class GitSyncStep : IBuildStep
{
    public string Phase => "git";

    public async Task ExecuteAsync(BuildContext ctx, CancellationToken ct)
    {
        var recipe = ctx.Recipe ?? throw new InvalidOperationException("GitSyncStep requires a recipe.");
        var gitRef = string.IsNullOrWhiteSpace(ctx.GitRefOverride) ? recipe.GitRef : ctx.GitRefOverride!;
        var repoUrl = recipe.GitRepoUrl;

        Func<string, Task> log = line => ctx.Sink.LineAsync(Phase, line);

        if (!Directory.Exists(Path.Combine(ctx.SourceDir, ".git")))
        {
            await ctx.Sink.PhaseAsync(Phase, $"Cloning {repoUrl}…");
            if (Directory.Exists(ctx.SourceDir)) Directory.Delete(ctx.SourceDir, recursive: true);
            Directory.CreateDirectory(ctx.SourceDir);
            await RunGitAsync(ctx, log, ct, "clone", repoUrl, ".");
        }
        else
        {
            await ctx.Sink.PhaseAsync(Phase, "Fetching latest refs…");
            await RunGitAsync(ctx, log, ct, "fetch", "--tags", "--force", "origin");
        }

        await ctx.Sink.PhaseAsync(Phase, $"Checking out {gitRef}…");
        await RunGitAsync(ctx, log, ct, "checkout", "--force", gitRef);
        // If it's a branch, fast-forward to the fetched tip.
        await TryRunGitAsync(ctx, log, ct, "reset", "--hard", $"origin/{gitRef}");
        await RunGitAsync(ctx, log, ct, "submodule", "update", "--init", "--recursive");

        ctx.ResolvedCommitSha = (await CaptureGitAsync(ctx, ct, "rev-parse", "HEAD"))?.Trim();
        var describe = (await CaptureGitAsync(ctx, ct, "describe", "--tags", "--always"))?.Trim();
        ctx.ResolvedVersionTag = (describe is not null ? ReleaseAssetResolver.ExtractBuildTag(describe) : null) ?? describe;
        await ctx.Sink.PhaseAsync(Phase, $"At {ctx.ResolvedCommitSha?[..Math.Min(7, ctx.ResolvedCommitSha?.Length ?? 0)]} ({ctx.ResolvedVersionTag}).");
    }

    private static async Task RunGitAsync(BuildContext ctx, Func<string, Task> log, CancellationToken ct, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, ctx.SourceDir, ctx.EnvSetupCommand, log, ct);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        if (result.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed with exit code {result.ExitCode}.");
    }

    private static async Task TryRunGitAsync(BuildContext ctx, Func<string, Task> log, CancellationToken ct, params string[] args)
    {
        try { await ProcessRunner.RunAsync("git", args, ctx.SourceDir, ctx.EnvSetupCommand, log, ct); }
        catch { /* optional step (ref is a tag/sha, not a branch) */ }
    }

    private static async Task<string?> CaptureGitAsync(BuildContext ctx, CancellationToken ct, params string[] args)
    {
        var sb = new System.Text.StringBuilder();
        var result = await ProcessRunner.RunAsync("git", args, ctx.SourceDir, ctx.EnvSetupCommand,
            line => { sb.AppendLine(line); return Task.CompletedTask; }, ct);
        return result.ExitCode == 0 ? sb.ToString() : null;
    }
}

/// <summary>Runs <c>cmake</c> configure with the recipe's generator, build type, and args.</summary>
public sealed class CMakeConfigureStep : IBuildStep
{
    public string Phase => "cmake-configure";

    public async Task ExecuteAsync(BuildContext ctx, CancellationToken ct)
    {
        var recipe = ctx.Recipe!;
        var args = new List<string> { "-S", ctx.SourceDir, "-B", ctx.BuildDir };
        if (!string.IsNullOrWhiteSpace(recipe.CMakeGenerator))
        {
            args.Add("-G");
            args.Add(recipe.CMakeGenerator!);
        }
        args.Add($"-DCMAKE_BUILD_TYPE={recipe.BuildConfig}");
        args.AddRange(recipe.CMakeArgs.Where(a => !string.IsNullOrWhiteSpace(a)));

        await ctx.Sink.PhaseAsync(Phase, $"cmake {string.Join(' ', args)}");
        var result = await ProcessRunner.RunAsync("cmake", args, ctx.WorkRoot, ctx.EnvSetupCommand,
            line => ctx.Sink.LineAsync(Phase, line), ct);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        if (result.ExitCode != 0) throw new InvalidOperationException($"cmake configure failed with exit code {result.ExitCode}.");
    }
}

/// <summary>Runs <c>cmake --build</c>.</summary>
public sealed class CMakeBuildStep : IBuildStep
{
    public string Phase => "cmake-build";

    public async Task ExecuteAsync(BuildContext ctx, CancellationToken ct)
    {
        var recipe = ctx.Recipe!;
        var args = new List<string> { "--build", ctx.BuildDir, "--config", recipe.BuildConfig, "-j" };

        await ctx.Sink.PhaseAsync(Phase, "Compiling — this can take several minutes…");
        var result = await ProcessRunner.RunAsync("cmake", args, ctx.WorkRoot, ctx.EnvSetupCommand,
            line => ctx.Sink.LineAsync(Phase, line), ct);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        if (result.ExitCode != 0) throw new InvalidOperationException($"cmake build failed with exit code {result.ExitCode}.");
    }
}

/// <summary>Copies the compiled binaries (and any extra globs) into the versioned output folder.</summary>
public sealed class CollectArtifactsStep : IBuildStep
{
    public string Phase => "collect";

    public Task ExecuteAsync(BuildContext ctx, CancellationToken ct)
    {
        Directory.CreateDirectory(ctx.OutputDir);

        // Single-config generators put binaries in build/bin; multi-config (VS) in build/bin/<Config>.
        var candidates = new[]
        {
            Path.Combine(ctx.BuildDir, "bin", ctx.Recipe!.BuildConfig),
            Path.Combine(ctx.BuildDir, "bin"),
        };
        var binDir = candidates.FirstOrDefault(Directory.Exists)
            ?? throw new InvalidOperationException($"No build output folder found under {ctx.BuildDir}.");

        int copied = 0;
        foreach (var file in Directory.EnumerateFiles(binDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(binDir, file);
            var dest = Path.Combine(ctx.OutputDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            copied++;
        }

        foreach (var glob in ctx.Recipe.ExtraArtifactGlobs.Where(g => !string.IsNullOrWhiteSpace(g)))
        {
            var (dir, pattern) = SplitGlob(Path.Combine(ctx.BuildDir, glob));
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
            {
                var dest = Path.Combine(ctx.OutputDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
                copied++;
            }
        }

        return ctx.Sink.PhaseAsync(Phase, $"Copied {copied} file(s) to {ctx.OutputDir}.");
    }

    private static (string Dir, string Pattern) SplitGlob(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var pattern = Path.GetFileName(path);
        return (dir, string.IsNullOrEmpty(pattern) ? "*" : pattern);
    }
}
