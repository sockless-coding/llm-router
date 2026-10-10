using System.IO.Compression;
using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Data;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Services;

/// <summary>
/// Strata installs: git checkouts of Strata's repository holding its Python frontend, its <c>.venv</c>
/// and an engine build — but no model. Models come from the model library and are prepared for
/// Strata when a preset first starts (see the Strata provider). Two kinds:
/// <list type="bullet">
/// <item>a release install (<see cref="EngineBuildSource.OfficialRelease"/>): one release tag with one of
/// its ready-made engine builds (CUDA, CUDA 12, AMD), side by side in its own folder like a llama.cpp
/// release — updated by checking out a newer tag and installing that release's same engine build;</item>
/// <item>an existing checkout the user set up themselves (<see cref="EngineBuildSource.GitCheckout"/>),
/// following <c>main</c> — updated like Strata's <c>UPDATE.bat</c>: <c>git pull --ff-only</c>, then
/// <c>setup.py --update</c>.</item>
/// </list>
/// </summary>
public partial class EngineBuildService
{
    /// <summary>
    /// Installs Strata release <paramref name="releaseTag"/> (null = latest) with the engine build in
    /// release asset <paramref name="assetName"/> into <c>&lt;install root&gt;/strata-&lt;tag&gt;-&lt;engine&gt;</c>
    /// on a background task: clones the release, creates its <c>.venv</c> with Strata's pinned Python
    /// packages, and installs the engine (checked against GitHub's SHA-256). Returns the new row's ID.
    /// </summary>
    public async Task<Guid> StartStrataReleaseInstallAsync(string? releaseTag, string? assetName, string? name)
    {
        var settings = await _settings.GetAsync();
        if (string.IsNullOrWhiteSpace(settings.InstallRootFolder))
            throw new InvalidOperationException("Set an engine install root folder (Engine Build Settings) before installing Strata releases.");

        var release = await GetStrataReleaseAsync(releaseTag);
        var tag = release.TagName;
        var asset = PickStrataEngineAsset(release, assetName);
        var variant = asset is null ? (StrataEngineVariant?)null : StrataLayout.VariantOfAsset(asset.Name);

        var folderName = variant is { } v ? $"strata-{tag}-{v.ToString().ToLowerInvariant()}" : $"strata-{tag}";
        var folder = Path.GetFullPath(Path.Combine(settings.InstallRootFolder, folderName));
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new InvalidOperationException($"'{folder}' already exists. Delete it, or add it as an existing checkout.");

        Guid buildId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            if (await context.EngineBuilds.AnyAsync(b => b.InstallPath == folder))
                throw new InvalidOperationException($"'{folder}' is already tracked.");

            var build = new EngineBuild
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(name) ? StrataReleaseName(tag, variant) : name.Trim(),
                Engine = ServerEngine.Strata,
                Source = EngineBuildSource.OfficialRelease,
                BackendType = variant == StrataEngineVariant.Hip ? BackendType.Hip : BackendType.Cuda,
                InstallPath = folder,
                VersionTag = tag,
                Status = EngineBuildStatus.Downloading,
                StatusMessage = "Installing…",
            };
            context.EngineBuilds.Add(build);
            await context.SaveChangesAsync();
            buildId = build.Id;
        }

        var workspaceRoot = WorkspaceRoot(settings);
        StartJob(buildId, ct => RunStrataJobAsync(buildId, folder, workspaceRoot, isNewInstall: true, async (sink, jobCt) =>
        {
            await sink.PhaseAsync("git", $"Cloning Strata {tag} into {folder}…");
            await RunCheckedAsync(sink, "git", "git", ["clone", "-c", "core.longpaths=true", "--branch", tag, StrataLayout.RepoUrl, folder],
                settings.InstallRootFolder, jobCt);

            await EnsureStrataVenvAsync(sink, folder, jobCt);
            await InstallStrataPackagesAsync(sink, folder, jobCt);

            if (asset is not null && variant is { } engineVariant)
                await InstallStrataEngineAsync(sink, buildId, folder, asset, engineVariant, workspaceRoot, jobCt);
            else
                await sink.LineAsync("engine", "No ready-made engine for this OS: Strata's setup compiles one the first time a model is prepared.");
        }, ct, finalize: build => SetStrataRelease(build, tag)));
        return buildId;
    }

    private static string StrataReleaseName(string tag, StrataEngineVariant? variant) =>
        variant is { } v ? $"Strata {tag} ({StrataLayout.Describe(v)})" : $"Strata {tag}";

    /// <summary>Records <paramref name="tag"/> as the release <paramref name="build"/> is on, renaming a default-named install.</summary>
    private static void SetStrataRelease(EngineBuild build, string tag)
    {
        if (build.VersionTag is { } old && build.Name.StartsWith($"Strata {old}", StringComparison.Ordinal))
            build.Name = $"Strata {tag}" + build.Name[$"Strata {old}".Length..];
        build.VersionTag = tag;
    }

    /// <summary>Release <paramref name="requested"/> (checked against Strata's tag format), or the latest release.</summary>
    private async Task<GitHubRelease> GetStrataReleaseAsync(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return await _github.GetLatestReleaseAsync(StrataLayout.Repo)
                ?? throw new InvalidOperationException("Could not reach GitHub to look up the latest Strata release.");

        var tag = requested.Trim();
        if (!StrataLayout.IsReleaseTag(tag))
            throw new InvalidOperationException($"'{tag}' isn't a Strata release tag (like v0.1.41).");
        return await _github.GetReleaseByTagAsync(StrataLayout.Repo, tag)
            ?? throw new InvalidOperationException($"Strata release {tag} wasn't found on GitHub.");
    }

    /// <summary>
    /// The release's engine asset called <paramref name="assetName"/>, which must be one of this OS's
    /// ready-made engines. On Windows one is required; elsewhere there are none and null is returned.
    /// </summary>
    private static GitHubReleaseAsset? PickStrataEngineAsset(GitHubRelease release, string? assetName)
    {
        var engines = release.Assets.Where(a => StrataLayout.VariantOfAsset(a.Name) is not null).ToList();
        if (string.IsNullOrWhiteSpace(assetName))
        {
            if (engines.Count == 0)
                return null;
            throw new InvalidOperationException("Pick which engine build to install (CUDA, CUDA 12 or AMD).");
        }

        return engines.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Strata {release.TagName} has no engine build '{assetName}' for this OS.");
    }

    /// <summary>
    /// Downloads a ready-made engine, checks it against the SHA-256 GitHub published for it, and
    /// installs it where Strata's setup expects it (<c>engine/</c>, or <c>engine-cuda12/</c>) — keeping
    /// the engine it replaces in <c>.previous</c>, as Strata does, so it can be rolled back.
    /// </summary>
    private async Task InstallStrataEngineAsync(
        BuildProgressSink sink, Guid buildId, string checkout, GitHubReleaseAsset asset, StrataEngineVariant variant,
        string workspaceRoot, CancellationToken ct)
    {
        var workRoot = Path.Combine(workspaceRoot, ".work", buildId.ToString("N"));
        var archive = Path.Combine(workRoot, "download", asset.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);

        await sink.PhaseAsync("download", $"Downloading the {StrataLayout.Describe(variant)} engine ({asset.Name})…");
        await _github.DownloadAssetAsync(asset.BrowserDownloadUrl, archive, sink.AsProgress(), ct);

        if (asset.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(archive);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (!string.Equals(actual, digest["sha256:".Length..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{asset.Name} doesn't match the SHA-256 GitHub published for it — not installed.");
            await sink.LineAsync("download", $"✔ SHA-256 matches GitHub's ({digest["sha256:".Length..][..12]}…)");
        }
        else
        {
            await sink.LineAsync("download", "GitHub published no checksum for this file; installing it unverified.");
        }

        var unpacked = Path.Combine(workRoot, "unpacked");
        TryDelete(unpacked);
        ZipFile.ExtractToDirectory(archive, unpacked);
        if (!File.Exists(Path.Combine(unpacked, StrataLayout.EngineExecutable)) || !File.Exists(Path.Combine(unpacked, "BUILD.json")))
            throw new InvalidOperationException($"{asset.Name} has no {StrataLayout.EngineExecutable} / BUILD.json at its top level.");

        var engineDir = Path.Combine(checkout, StrataLayout.EngineFolder(variant));
        var previous = Path.Combine(engineDir, ".previous");
        Directory.CreateDirectory(engineDir);
        TryDelete(previous);
        var old = Directory.EnumerateFileSystemEntries(engineDir).ToList();
        if (old.Count > 0)
        {
            Directory.CreateDirectory(previous);
            foreach (var entry in old)
                MoveEntry(entry, Path.Combine(previous, Path.GetFileName(entry)));
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(unpacked))
            MoveEntry(entry, Path.Combine(engineDir, Path.GetFileName(entry)));

        TryDelete(unpacked);
        TryDelete(Path.GetDirectoryName(archive)!);
        await sink.LineAsync("engine", $"✔ engine {StrataLayout.ReadEngineVersion(checkout) ?? "?"} installed in {engineDir}");
    }

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination, overwrite: true);
    }

    /// <summary>Installs Strata's pinned Python packages (<c>requirements.txt</c>) into its <c>.venv</c>, as its setup does.</summary>
    private static async Task InstallStrataPackagesAsync(BuildProgressSink sink, string checkout, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(checkout, "requirements.txt")))
            return;   // older releases: their setup installs the packages the first time a model is prepared
        await sink.PhaseAsync("python", "Installing Strata's Python packages (requirements.txt)…");
        await RunCheckedAsync(sink, "python", StrataLayout.PythonPath(checkout),
            ["-m", "pip", "install", "--disable-pip-version-check", "-r", "requirements.txt"], checkout, ct);
    }

    private sealed record PythonCommand(string Executable, IReadOnlyList<string> PrefixArgs);

    /// <summary>
    /// A Python 3.10+ to create a new install's <c>.venv</c> with: another tracked Strata install's
    /// (known to work for Strata), else the <c>py</c> launcher or <c>python</c> on PATH.
    /// </summary>
    private async Task<PythonCommand?> FindBasePythonAsync(string excludingFolder, CancellationToken ct)
    {
        var candidates = new List<PythonCommand>();
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            var installs = await context.EngineBuilds
                .Where(b => b.Engine == ServerEngine.Strata && b.InstallPath != excludingFolder)
                .OrderByDescending(b => b.BuildCompletedAt)
                .Select(b => b.InstallPath)
                .ToListAsync(ct);
            candidates.AddRange(installs
                .Select(StrataLayout.PythonPath)
                .Where(File.Exists)
                .Select(p => new PythonCommand(p, [])));
        }
        if (OperatingSystem.IsWindows())
            candidates.Add(new PythonCommand("py", ["-3"]));
        candidates.Add(new PythonCommand("python", []));
        if (!OperatingSystem.IsWindows())
            candidates.Add(new PythonCommand("python3", []));

        foreach (var candidate in candidates)
        {
            try
            {
                var result = await ProcessRunner.RunAsync(candidate.Executable,
                    [.. candidate.PrefixArgs, "-c", "import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)"],
                    excludingFolder, null, _ => Task.CompletedTask, ct);
                if (result.ExitCode == 0)
                    return candidate;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not installed / not runnable: try the next one.
            }
        }
        return null;
    }

    /// <summary>Creates the checkout's <c>.venv</c> (as <c>START-HERE.bat</c> would) unless it already has one.</summary>
    private async Task EnsureStrataVenvAsync(BuildProgressSink sink, string folder, CancellationToken ct)
    {
        if (File.Exists(StrataLayout.PythonPath(folder)))
            return;

        await sink.PhaseAsync("python", "Creating Strata's Python environment (.venv)…");
        var basePython = await FindBasePythonAsync(folder, ct)
            ?? throw new InvalidOperationException(
                "No Python 3.10+ found (no other Strata install, and neither 'py -3' nor 'python' on PATH). Install Python 3.10 or newer.");
        await RunCheckedAsync(sink, "python", basePython.Executable,
            [.. basePython.PrefixArgs, "-m", "venv", Path.Combine(folder, ".venv")], folder, ct);
    }

    /// <summary>Starts tracking an existing Strata checkout. Returns the new row's ID.</summary>
    public async Task<Guid> RegisterStrataCheckoutAsync(string folder, string? name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("Enter the folder of your Strata checkout.");

        folder = Path.GetFullPath(folder.Trim());
        if (!Directory.Exists(folder))
            throw new InvalidOperationException($"The folder '{folder}' does not exist.");
        if (!StrataLayout.IsCheckout(folder))
            throw new InvalidOperationException($"'{folder}' doesn't look like a Strata checkout (no {StrataLayout.ServerScriptRelativePath} / {StrataLayout.SetupScript}).");

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        if (await context.EngineBuilds.AnyAsync(b => b.InstallPath == folder, ct))
            throw new InvalidOperationException($"'{folder}' is already tracked.");

        var build = new EngineBuild
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? $"Strata ({Path.GetFileName(folder)})" : name.Trim(),
            Engine = ServerEngine.Strata,
            Source = EngineBuildSource.GitCheckout,
            InstallPath = folder,
            BuildCompletedAt = DateTime.UtcNow,
        };
        await ApplyStrataStateAsync(build, ct);

        context.EngineBuilds.Add(build);
        await context.SaveChangesAsync(ct);
        return build.Id;
    }

    /// <summary>Runs <c>setup.py --rollback-engine</c>: the engine kept from before the last update becomes the installed one again.</summary>
    public async Task<Guid> StartStrataRollbackAsync(Guid buildId)
    {
        var settings = await _settings.GetAsync();
        string installPath;
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            var build = await LoadIdleStrataBuildAsync(context, buildId);
            await EnsureNoRunningServersAsync(context, build);

            installPath = build.InstallPath;
            build.Status = EngineBuildStatus.Building;
            build.StatusMessage = "Rolling back the engine…";
            await context.SaveChangesAsync();
        }

        StartJob(buildId, ct => RunStrataJobAsync(buildId, installPath, WorkspaceRoot(settings), isNewInstall: false, async (sink, jobCt) =>
        {
            await sink.PhaseAsync("setup", "Restoring the previous engine (setup.py --rollback-engine)…");
            await RunStrataSetupAsync(sink, installPath,
                ["--rollback-engine", .. StrataLayout.SetupFlags(StrataLayout.InstalledEngine(installPath))], jobCt);
        }, ct));
        return buildId;
    }

    /// <summary>Re-reads a Strata checkout's version, commit and engine (e.g. after running Strata's own scripts by hand).</summary>
    public async Task RefreshStrataAsync(Guid buildId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var build = await LoadIdleStrataBuildAsync(context, buildId);
        await ApplyStrataStateAsync(build, ct);
        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The in-place update behind <see cref="StartUpdateAsync"/> for a Strata install. A release
    /// install moves to <paramref name="releaseTag"/>: that tag's code and packages, and the same engine
    /// build from that release. A checkout following <c>main</c> pulls and runs Strata's own
    /// <c>setup.py --update</c>, which updates its engine.
    /// </summary>
    private Task RunStrataUpdateAsync(Guid buildId, string checkout, string workspaceRoot, string? releaseTag, CancellationToken ct) =>
        RunStrataJobAsync(buildId, checkout, workspaceRoot, isNewInstall: false, async (sink, jobCt) =>
        {
            if (releaseTag is not null)
            {
                var variant = StrataLayout.InstalledEngine(checkout);
                var release = await GetStrataReleaseAsync(releaseTag);

                await sink.PhaseAsync("git", $"Checking out Strata {releaseTag}…");
                await RunCheckedAsync(sink, "git", "git", ["fetch", "--tags", "--force", "origin"], checkout, jobCt);
                await RunCheckedAsync(sink, "git", "git", ["checkout", "--force", releaseTag], checkout, jobCt);
                await InstallStrataPackagesAsync(sink, checkout, jobCt);

                var asset = variant is { } v
                    ? release.Assets.FirstOrDefault(a => StrataLayout.VariantOfAsset(a.Name) == v)
                    : null;
                if (asset is not null)
                    await InstallStrataEngineAsync(sink, buildId, checkout, asset, variant!.Value, workspaceRoot, jobCt);
                else
                    await sink.LineAsync("engine", $"No ready-made {(variant is { } vv ? StrataLayout.Describe(vv) + " " : "")}engine in {releaseTag}: Strata's setup updates it when a model is next prepared.");
                return;
            }

            if (StrataLayout.IsGitClone(checkout))
            {
                await sink.PhaseAsync("git", "Getting the newest Strata (git pull --ff-only)…");
                await RunCheckedAsync(sink, "git", "git", ["pull", "--ff-only"], checkout, jobCt);
            }
            else
            {
                await sink.PhaseAsync("git",
                    "This copy of Strata wasn't made with git, so its code can't be pulled — download a newer one from " +
                    $"https://github.com/{StrataLayout.Repo}. Checking this copy's engine and settings meanwhile…");
            }

            await sink.PhaseAsync("setup", "Updating the engine, Python packages and model configs (setup.py --update)…");
            await RunStrataSetupAsync(sink, checkout, ["--update"], jobCt);
        }, ct, finalize: releaseTag is null ? null : build => SetStrataRelease(build, releaseTag));

    /// <summary>
    /// Runs a Strata job with the usual build bookkeeping: a <c>build.log</c> + live progress, the
    /// row refreshed from disk afterwards. A failed job on an existing install leaves it as the folder
    /// now is (Ready, normally) with the failure noted; a failed new install is an
    /// <see cref="EngineBuildStatus.Error"/>.
    /// </summary>
    private async Task RunStrataJobAsync(
        Guid buildId, string checkout, string workspaceRoot, bool isNewInstall,
        Func<BuildProgressSink, CancellationToken, Task> body, CancellationToken ct,
        Action<EngineBuild>? finalize = null)
    {
        var workRoot = Path.Combine(workspaceRoot, ".work", buildId.ToString("N"));
        Directory.CreateDirectory(workRoot);
        var sink = new BuildProgressSink(buildId, _progressPublisher, Path.Combine(workRoot, "build.log"), _logger);

        try
        {
            await body(sink, ct);

            string summary;
            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
                var build = await context.EngineBuilds.FindAsync(new object?[] { buildId }, ct)
                    ?? throw new InvalidOperationException("The install was removed while the job ran.");
                await ApplyStrataStateAsync(build, ct);
                finalize?.Invoke(build);
                build.BuildCompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(ct);
                summary = $"Strata {build.VersionTag ?? "(unknown version)"}" +
                    (build.CommitSha is { Length: >= 7 } sha ? $" at {sha[..7]}" : "") +
                    (StrataLayout.ReadEngineVersion(checkout) is { } ev ? $", engine {ev}" : "") + ".";
            }

            await sink.CompletedAsync(summary);
        }
        catch (OperationCanceledException)
        {
            if (isNewInstall) await FailNewInstallAsync(buildId, checkout, "Install cancelled.");
            else await RestoreStrataAfterFailureAsync(buildId, "Cancelled — Strata was left as it was.");
            await sink.ErrorAsync("Cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Strata job for {BuildId} failed.", buildId);
            if (isNewInstall) await FailNewInstallAsync(buildId, checkout, ex.Message);
            else await RestoreStrataAfterFailureAsync(buildId, $"Last attempt failed: {ex.Message}");
            await sink.ErrorAsync(ex.Message);
        }
        finally
        {
            _active.TryRemove(buildId, out _);
        }
    }

    /// <summary>
    /// A new install that failed: marks it <see cref="EngineBuildStatus.Error"/> and removes the
    /// half-made folder so the install can simply be retried — unless it somehow holds model data,
    /// which is never deleted.
    /// </summary>
    private async Task FailNewInstallAsync(Guid buildId, string folder, string message)
    {
        if (!StrataLayout.HoldsModelData(folder))
            TryDelete(folder);
        await MarkErrorAsync(buildId, message);
    }

    /// <summary>
    /// A job on an existing install failed: the folder stays usable (an engine is only replaced once
    /// the new one is fully downloaded and verified; Strata's setup keeps its previous engine on a failed
    /// update), so the row goes back to whatever the folder now is, with the failure noted.
    /// </summary>
    private async Task RestoreStrataAfterFailureAsync(Guid buildId, string message)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var build = await context.EngineBuilds.FindAsync(buildId);
        if (build is null) return;
        await ApplyStrataStateAsync(build, CancellationToken.None);
        build.StatusMessage = build.Status == EngineBuildStatus.NeedsSetup
            ? $"{message} {build.StatusMessage}"
            : message;
        await context.SaveChangesAsync();
    }

    /// <summary>Runs Strata's <c>setup.py</c> from its own <c>.venv</c>, unbuffered so its output streams into the log.</summary>
    private static Task RunStrataSetupAsync(BuildProgressSink sink, string checkout, IReadOnlyList<string> args, CancellationToken ct)
    {
        var python = StrataLayout.PythonPath(checkout);
        if (!File.Exists(python))
            throw new InvalidOperationException($"Strata's Python environment (.venv) is missing in {checkout}.");

        return RunCheckedAsync(sink, "setup", python, ["-u", StrataLayout.SetupScript, .. args], checkout, ct);
    }

    private static async Task RunCheckedAsync(
        BuildProgressSink sink, string phase, string executable, IReadOnlyList<string> args, string workingDirectory, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(executable, args, workingDirectory, null, line => sink.LineAsync(phase, line), ct);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} {string.Join(' ', args)} failed with exit code {result.ExitCode} (see the log above).");
    }

    /// <summary>Reads the checkout's version, commit, engine and readiness onto <paramref name="build"/>.</summary>
    private async Task ApplyStrataStateAsync(EngineBuild build, CancellationToken ct)
    {
        var checkout = build.InstallPath;
        if (!StrataLayout.IsCheckout(checkout))
        {
            build.Status = EngineBuildStatus.Missing;
            build.StatusMessage = "Install folder is no longer on disk.";
            return;
        }

        // A release install's version is the tag it was checked out at; anything else reports its source version.
        if (build.Source != EngineBuildSource.OfficialRelease || build.VersionTag is null)
            build.VersionTag = StrataLayout.ReadSourceVersion(checkout) is { } version ? "v" + version : null;
        build.CommitSha = StrataLayout.IsGitClone(checkout) ? await ReadGitHeadAsync(checkout, ct) : null;
        build.BackendType = StrataInstallInfo.DetectBackend(checkout);

        if (StrataLayout.IsSetUp(checkout))
        {
            build.Status = EngineBuildStatus.Ready;
            build.StatusMessage = null;
        }
        else
        {
            build.Status = EngineBuildStatus.NeedsSetup;
            build.StatusMessage = File.Exists(StrataLayout.PythonPath(checkout))
                ? "No Strata engine is installed in this folder. Install a Strata release from the Engines page instead (it comes with its engine)."
                : "This folder has no Python environment (.venv) or engine yet. Install a Strata release from the Engines page instead.";
        }
    }

    private async Task<string?> ReadGitHeadAsync(string checkout, CancellationToken ct)
    {
        try
        {
            string? last = null;
            var result = await ProcessRunner.RunAsync("git", ["rev-parse", "HEAD"], checkout, null,
                line => { if (!string.IsNullOrWhiteSpace(line)) last = line.Trim(); return Task.CompletedTask; }, ct);
            return result.ExitCode == 0 && last is { Length: 40 } && last.All(char.IsAsciiHexDigit) ? last : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the git commit of {Checkout}", checkout);
            return null;
        }
    }

    private async Task<EngineBuild> LoadIdleStrataBuildAsync(LRDbContext context, Guid buildId)
    {
        var build = await context.EngineBuilds.FindAsync(buildId)
            ?? throw new InvalidOperationException("Install not found.");
        if (build.Engine != ServerEngine.Strata)
            throw new InvalidOperationException("That isn't a Strata install.");
        if (_active.ContainsKey(buildId) ||
            build.Status is EngineBuildStatus.Downloading or EngineBuildStatus.Building or EngineBuildStatus.Pending)
            throw new InvalidOperationException("A job is already running on this install.");
        return build;
    }
}
