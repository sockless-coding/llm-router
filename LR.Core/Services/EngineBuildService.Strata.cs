using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Data;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Services;

/// <summary>
/// Strata installs. A Strata install is a git checkout of its repository that Strata's own setup
/// prepares and updates, and the router drives the same commands Strata's scripts run. Two kinds:
/// <list type="bullet">
/// <item>a checkout following <c>main</c> (<see cref="EngineBuildSource.GitCheckout"/>) — updated like
/// <c>UPDATE.bat</c>/<c>update.sh</c>: <c>git pull --ff-only</c>, then <c>setup.py --update</c>;</item>
/// <item>a release install (<see cref="EngineBuildSource.OfficialRelease"/>) pinned to a release tag in
/// its own versioned folder — updated by checking out a newer tag, then <c>setup.py --update</c>, which
/// installs that release's ready-made engine.</item>
/// </list>
/// <c>setup.py --update</c> never asks and never touches the model files. A new release install is set
/// up the way Strata sets up a newly unpacked copy: like the most recent earlier install on this PC
/// (same model and settings, model files reused from the shared <c>Strata-data</c> folder) — only when
/// there is none does the user have to run Strata's interactive first-time setup.
/// </summary>
public partial class EngineBuildService
{
    /// <summary>
    /// Installs Strata release <paramref name="releaseTag"/> (null = latest) into
    /// <c>&lt;install root&gt;/strata-&lt;tag&gt;</c> on a background task: clones it at that tag, creates its
    /// Python environment, and runs Strata's setup unattended (see the class summary). Returns the new row's ID.
    /// </summary>
    public async Task<Guid> StartStrataReleaseInstallAsync(string? releaseTag, string? name)
    {
        var settings = await _settings.GetAsync();
        if (string.IsNullOrWhiteSpace(settings.InstallRootFolder))
            throw new InvalidOperationException("Set an engine install root folder (Engine Build Settings) before installing Strata releases.");

        var tag = await ResolveStrataReleaseTagAsync(releaseTag);
        var folder = Path.GetFullPath(Path.Combine(settings.InstallRootFolder, $"strata-{tag}"));
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
                Name = string.IsNullOrWhiteSpace(name) ? $"Strata {tag}" : name.Trim(),
                Engine = ServerEngine.Strata,
                Source = EngineBuildSource.OfficialRelease,
                BackendType = BackendType.Cuda,
                InstallPath = folder,
                VersionTag = tag,
                Status = EngineBuildStatus.Downloading,
                StatusMessage = "Installing…",
            };
            context.EngineBuilds.Add(build);
            await context.SaveChangesAsync();
            buildId = build.Id;
        }

        StartJob(buildId, ct => RunStrataJobAsync(buildId, folder, WorkspaceRoot(settings), isNewInstall: true, async (sink, jobCt) =>
        {
            await sink.PhaseAsync("git", $"Cloning Strata {tag} into {folder}…");
            await RunCheckedAsync(sink, "git", "git", ["clone", "-c", "core.longpaths=true", "--branch", tag, StrataLayout.RepoUrl, folder],
                settings.InstallRootFolder, jobCt);

            await sink.PhaseAsync("python", "Creating Strata's Python environment (.venv)…");
            var basePython = await FindBasePythonAsync(folder, jobCt)
                ?? throw new InvalidOperationException(
                    "No Python 3.10+ found (no other Strata install, and neither 'py -3' nor 'python' on PATH). Install Python, or run " +
                    $"{StrataLayout.FirstTimeSetupCommand} in {folder}, which installs Python for you.");
            await RunCheckedAsync(sink, "python", basePython.Executable,
                [.. basePython.PrefixArgs, "-m", "venv", Path.Combine(folder, ".venv")], folder, jobCt);

            // A setup that can't finish on its own (no earlier install to copy, or it needs an answer)
            // leaves a good checkout behind: the row ends up NeedsSetup, and the log says why.
            await sink.PhaseAsync("setup", "Setting Strata up like your most recent install (same model and settings, model files reused, this release's engine)…");
            try
            {
                await RunCheckedAsync(sink, "setup", StrataLayout.PythonPath(folder), ["-u", "-c", UnattendedSetupDriver], folder, jobCt,
                    displayCommand: "Strata's setup (unattended)");
            }
            catch (InvalidOperationException ex)
            {
                await sink.LineAsync("setup", $"✖ Strata's setup couldn't finish on its own: {ex.Message}");
                await sink.LineAsync("setup", $"  Run {StrataLayout.FirstTimeSetupCommand} in {folder} to answer its questions, then press Refresh.");
            }
        }, ct, finalize: build => SetStrataRelease(build, tag)));
        return buildId;
    }

    /// <summary>
    /// Runs Strata's <c>setup.py</c> as a plain start of a new copy would — which, for a folder with no
    /// model yet and an earlier install on this PC, sets it up like that install without asking — but
    /// with its final step (starting the model server) replaced by a no-op. With no earlier install,
    /// setup's first question meets the closed stdin and it exits with an error instead of hanging.
    /// </summary>
    private const string UnattendedSetupDriver =
        "import sys\n" +
        "sys.argv = ['setup.py']\n" +
        "sys.path.insert(0, '.')\n" +
        "import setup\n" +
        "if not callable(getattr(setup, 'start', None)):\n" +
        "    sys.exit('this Strata version has no setup.start(): run its setup yourself')\n" +
        "setup.start = lambda *a, **k: 0\n" +
        "sys.exit(setup.main())\n";

    /// <summary>Records <paramref name="tag"/> as the release <paramref name="build"/> is on, renaming a default-named install.</summary>
    private static void SetStrataRelease(EngineBuild build, string tag)
    {
        if (build.VersionTag is { } old && build.Name == $"Strata {old}")
            build.Name = $"Strata {tag}";
        build.VersionTag = tag;
    }

    /// <summary><paramref name="requested"/> checked against Strata's tag format, or the latest release's tag when null.</summary>
    private async Task<string> ResolveStrataReleaseTagAsync(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var tag = requested.Trim();
            if (!StrataLayout.IsReleaseTag(tag))
                throw new InvalidOperationException($"'{tag}' isn't a Strata release tag (like v0.1.41).");
            return tag;
        }

        var latest = await _github.GetLatestReleaseAsync(StrataLayout.Repo)
            ?? throw new InvalidOperationException("Could not reach GitHub to look up the latest Strata release.");
        return latest.TagName;
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

    /// <summary>
    /// Clones Strata into <c>&lt;install root&gt;/&lt;folderName&gt;</c> on a background task. The
    /// clone ends up <see cref="EngineBuildStatus.NeedsSetup"/> until the user runs Strata's own
    /// first-time setup there. Returns the new row's ID.
    /// </summary>
    public async Task<Guid> StartStrataCloneAsync(string? folderName, string? name)
    {
        var settings = await _settings.GetAsync();
        if (string.IsNullOrWhiteSpace(settings.InstallRootFolder))
            throw new InvalidOperationException("Set an engine install root folder (Engine Build Settings) before cloning Strata.");

        folderName = string.IsNullOrWhiteSpace(folderName) ? "strata" : SanitizeFolder(folderName.Trim());
        var folder = Path.GetFullPath(Path.Combine(settings.InstallRootFolder, folderName));
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new InvalidOperationException($"'{folder}' already exists. Pick another folder name, or add it as an existing checkout.");

        Guid buildId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            if (await context.EngineBuilds.AnyAsync(b => b.InstallPath == folder))
                throw new InvalidOperationException($"'{folder}' is already tracked.");

            var build = new EngineBuild
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(name) ? $"Strata ({folderName})" : name.Trim(),
                Engine = ServerEngine.Strata,
                Source = EngineBuildSource.GitCheckout,
                BackendType = BackendType.Cuda,
                InstallPath = folder,
                Status = EngineBuildStatus.Downloading,
                StatusMessage = "Cloning…",
            };
            context.EngineBuilds.Add(build);
            await context.SaveChangesAsync();
            buildId = build.Id;
        }

        var workspaceRoot = WorkspaceRoot(settings);
        StartJob(buildId, ct => RunStrataJobAsync(buildId, folder, workspaceRoot, isNewInstall: true, async (sink, jobCt) =>
        {
            await sink.PhaseAsync("git", $"Cloning {StrataLayout.RepoUrl} into {folder}…");
            await RunCheckedAsync(sink, "git", "git", ["clone", "-c", "core.longpaths=true", "--branch", StrataLayout.Branch, StrataLayout.RepoUrl, folder],
                settings.InstallRootFolder, jobCt);
        }, ct));
        return buildId;
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
            await RunStrataSetupAsync(sink, installPath, "--rollback-engine", jobCt);
        }, ct));
        return buildId;
    }

    /// <summary>Re-reads a Strata checkout's version, commit and setup state (e.g. after running Strata's own scripts by hand).</summary>
    public async Task RefreshStrataAsync(Guid buildId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var build = await LoadIdleStrataBuildAsync(context, buildId);
        await ApplyStrataStateAsync(build, ct);
        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The in-place update behind <see cref="StartUpdateAsync"/> for a Strata install: to
    /// <paramref name="releaseTag"/> for a release install, else to the newest <c>main</c>.
    /// </summary>
    private Task RunStrataUpdateAsync(Guid buildId, string checkout, string workspaceRoot, string? releaseTag, CancellationToken ct) =>
        RunStrataJobAsync(buildId, checkout, workspaceRoot, isNewInstall: false, async (sink, jobCt) =>
        {
            if (releaseTag is not null)
            {
                await sink.PhaseAsync("git", $"Checking out Strata {releaseTag}…");
                await RunCheckedAsync(sink, "git", "git", ["fetch", "--tags", "--force", "origin"], checkout, jobCt);
                await RunCheckedAsync(sink, "git", "git", ["checkout", "--force", releaseTag], checkout, jobCt);
            }
            else if (StrataLayout.IsGitClone(checkout))
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
            await RunStrataSetupAsync(sink, checkout, "--update", jobCt);
        }, ct, finalize: releaseTag is null ? null : build => SetStrataRelease(build, releaseTag));

    /// <summary>
    /// Runs a Strata job with the usual build bookkeeping: a <c>build.log</c> + live progress, the
    /// row refreshed from disk afterwards. A failed job on an existing install leaves it
    /// <see cref="EngineBuildStatus.Ready"/> with the failure noted (Strata's own steps keep the
    /// previous engine when they fail); a failed clone is an <see cref="EngineBuildStatus.Error"/>.
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
                    (build.Status == EngineBuildStatus.NeedsSetup ? $" — now run {StrataLayout.FirstTimeSetupCommand} in {checkout} to finish setting it up." : ".");
            }

            await sink.CompletedAsync(summary);
        }
        catch (OperationCanceledException)
        {
            if (isNewInstall) await FailNewInstallAsync(buildId, checkout, "Install cancelled.");
            else await RestoreReadyAfterFailedUpdateAsync(buildId, "Cancelled — Strata was left as it was.");
            await sink.ErrorAsync("Cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Strata job for {BuildId} failed.", buildId);
            if (isNewInstall) await FailNewInstallAsync(buildId, checkout, ex.Message);
            else await RestoreReadyAfterFailedUpdateAsync(buildId, $"Last attempt failed: {ex.Message}");
            await sink.ErrorAsync(ex.Message);
        }
        finally
        {
            _active.TryRemove(buildId, out _);
        }
    }

    /// <summary>
    /// A new install that failed: marks it <see cref="EngineBuildStatus.Error"/> and removes the
    /// half-made folder so the install can simply be retried — unless Strata already put model files
    /// in it (it does when it can't use a data folder next to it), which are never deleted.
    /// </summary>
    private async Task FailNewInstallAsync(Guid buildId, string folder, string message)
    {
        if (!StrataLayout.HoldsModelData(folder))
            TryDelete(folder);
        await MarkErrorAsync(buildId, message);
    }

    /// <summary>Runs Strata's <c>setup.py</c> with <paramref name="option"/> from its own <c>.venv</c>, unbuffered so its output streams into the log.</summary>
    private static Task RunStrataSetupAsync(BuildProgressSink sink, string checkout, string option, CancellationToken ct)
    {
        var python = StrataLayout.PythonPath(checkout);
        if (!File.Exists(python))
            throw new InvalidOperationException(
                $"Strata's Python environment is missing — run {StrataLayout.FirstTimeSetupCommand} in {checkout} once to set Strata up.");

        return RunCheckedAsync(sink, "setup", python, ["-u", StrataLayout.SetupScript, option], checkout, ct);
    }

    private static async Task RunCheckedAsync(
        BuildProgressSink sink, string phase, string executable, IReadOnlyList<string> args, string workingDirectory, CancellationToken ct,
        string? displayCommand = null)
    {
        var result = await ProcessRunner.RunAsync(executable, args, workingDirectory, null, line => sink.LineAsync(phase, line), ct);
        if (result.Cancelled) throw new OperationCanceledException(ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"{displayCommand ?? $"{Path.GetFileName(executable)} {string.Join(' ', args)}"} failed with exit code {result.ExitCode} (see the log above).");
    }

    /// <summary>Reads the checkout's version, commit, GPU backend and setup state onto <paramref name="build"/>.</summary>
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
            build.StatusMessage = $"Run {StrataLayout.FirstTimeSetupCommand} in this folder to install Strata's engine and a model, then press Refresh.";
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
