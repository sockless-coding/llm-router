using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;

using LR.Core.Agents;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Core.Services.EngineBuilds;

namespace LR.Agent;

/// <summary>
/// Runs the router's long jobs on this machine — engine installs/updates (the very same
/// <see cref="IBuildStep"/> pipelines the router runs locally, so release asset selection naturally
/// matches this machine's OS/arch), Hugging Face downloads, copies from the router's library,
/// metadata reads and deletes — reporting progress and the outcome back under the job's id.
/// </summary>
public sealed class JobRunner
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();
    private readonly AgentConfig _config;
    private readonly AgentSession _session;
    private readonly ILogger<JobRunner> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public JobRunner(AgentConfig config, AgentSession session, ILogger<JobRunner> logger, ILoggerFactory loggerFactory)
    {
        _config = config;
        _session = session;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public List<Guid> ActiveJobIds() => _active.Keys.ToList();

    public void Start(AgentJobMessage job)
    {
        var cts = new CancellationTokenSource();
        if (!_active.TryAdd(job.JobId, cts))
        {
            _logger.LogInformation("Job {JobId} is already running; ignoring the duplicate.", job.JobId);
            return;
        }

        _ = Task.Run(async () =>
        {
            var completion = new JobCompletedMessage { JobId = job.JobId };
            try
            {
                _logger.LogInformation("Starting {Job} {JobId}.", job.GetType().Name, job.JobId);
                completion.Result = job switch
                {
                    InstallEngineJob install => await InstallEngineAsync(install, cts.Token),
                    DownloadModelJob download => await DownloadModelAsync(download, cts.Token),
                    PullModelJob pull => await PullModelAsync(pull, cts.Token),
                    ReadModelMetadataJob read => await ReadMetadataAsync(read.FilePath, cts.Token),
                    DeletePathJob delete => await DeleteAsync(delete, cts.Token),
                    _ => throw new NotSupportedException($"This agent doesn't know how to run {job.GetType().Name}."),
                };
                completion.Success = true;
            }
            catch (OperationCanceledException)
            {
                completion.Cancelled = true;
                completion.Error = "Cancelled.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job {JobId} failed.", job.JobId);
                completion.Error = ex.Message;
            }
            finally
            {
                _active.TryRemove(job.JobId, out _);
            }
            await _session.SendCompletionAsync(completion);
        });
    }

    public void Cancel(Guid jobId)
    {
        if (_active.TryGetValue(jobId, out var cts))
            cts.Cancel();
    }

    // ==================== ENGINES ====================

    /// <summary>vLLM installs with pip, not from an archive: the same steps the router runs for itself (<see cref="VllmInstaller"/>).</summary>
    private async Task<AgentJobResult> InstallVllmAsync(InstallEngineJob job, CancellationToken ct)
    {
        var workRoot = Path.Combine(_config.ResolvedDataDirectory, ".work", job.JobId.ToString("N"));
        Directory.CreateDirectory(workRoot);
        var sink = new BuildProgressSink(job.JobId, new ForwardingBuildPublisher(_session), Path.Combine(workRoot, "build.log"),
            _loggerFactory.CreateLogger<BuildProgressSink>());

        var request = new VllmInstallRequest(job.Backend, job.ReleaseTag, job.OutputDir, job.WslDistro);
        try
        {
            var installed = await VllmInstaller.InstallAsync(request, sink, ct);
            await sink.CompletedAsync($"vLLM {installed.VersionTag} is ready in {installed.InstallPath}.");
            return new AgentJobResult
            {
                InstallPath = installed.InstallPath,
                VersionTag = installed.VersionTag,
                TargetOs = installed.TargetOs,
                TargetArch = installed.TargetArch,
                SizeBytes = installed.SizeBytes,
            };
        }
        catch when (!job.InPlace)
        {
            // Like on the router: a new install that failed leaves nothing behind, so it can be retried.
            try { await VllmInstaller.DeleteAsync(await VllmInstaller.ResolveInstallPathAsync(request, CancellationToken.None)); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not clean up the failed vLLM install for job {JobId}.", job.JobId); }
            throw;
        }
    }

    private async Task<AgentJobResult> InstallEngineAsync(InstallEngineJob job, CancellationToken ct)
    {
        if (job.Engine == ServerEngine.Vllm)
            return await InstallVllmAsync(job, ct);

        var profile = EngineReleaseProfile.For(job.Engine)
            ?? throw new NotSupportedException($"{job.Engine} can't be installed on an agent.");
        var github = new GitHubReleaseClient(CreateHttpClient(), new TokenSettings(_session), _loggerFactory.CreateLogger<GitHubReleaseClient>());

        var workRoot = Path.Combine(_config.ResolvedDataDirectory, ".work", job.JobId.ToString("N"));
        Directory.CreateDirectory(workRoot);
        var staging = Path.Combine(workRoot, "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

        string? sharedSrc = null;
        if (job.Recipe is not null)
        {
            var repoKey = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(job.Recipe.GitRepoUrl)))[..12].ToLowerInvariant();
            sharedSrc = Path.Combine(_config.ResolvedDataDirectory, ".src", repoKey);
        }

        var sink = new BuildProgressSink(job.JobId, new ForwardingBuildPublisher(_session), Path.Combine(workRoot, "build.log"),
            _loggerFactory.CreateLogger<BuildProgressSink>());

        var ctx = new BuildContext
        {
            BuildId = job.JobId,
            Repo = profile.Repo,
            BackendType = job.Backend,
            Profile = profile,
            Recipe = job.Recipe,
            GitRefOverride = job.Recipe is not null && !string.IsNullOrWhiteSpace(job.GitRef) ? job.GitRef : null,
            ReleaseTag = job.Recipe is null && !string.IsNullOrWhiteSpace(job.ReleaseTag) ? job.ReleaseTag : null,
            WorkRoot = workRoot,
            OutputDir = job.InPlace ? staging : job.OutputDir,
            SourceDirOverride = sharedSrc,
            EnvSetupCommand = job.Recipe?.EnvironmentSetupCommand,
            Sink = sink,
        };

        IBuildStep[] pipeline = job.Recipe is null
            ? [new ResolveReleaseAssetStep(github), new DownloadArchiveStep(github), new ExtractArchiveStep(), new FinalizeBuildStep()]
            : [new GitSyncStep(), new CMakeConfigureStep(), new CMakeBuildStep(), new CollectArtifactsStep(), new FinalizeBuildStep()];

        await EngineBuildRunner.RunAsync(pipeline, ctx, ct);

        var installPath = ctx.OutputDir;
        if (job.InPlace)
        {
            await sink.PhaseAsync("swap", $"Swapping the new version into {job.OutputDir}…");
            EngineBuildService.MirrorDirectory(ctx.OutputDir, job.OutputDir);
            installPath = job.OutputDir;
        }

        await sink.CompletedAsync($"Installed {ctx.ResolvedVersionTag ?? profile.DisplayName} to {installPath}.");
        TryDeleteDirectory(staging);
        TryDeleteDirectory(Path.Combine(workRoot, "download"));
        TryDeleteDirectory(Path.Combine(workRoot, "extract"));
        TryDeleteDirectory(Path.Combine(workRoot, "build"));

        return new AgentJobResult
        {
            InstallPath = installPath,
            VersionTag = ctx.ResolvedVersionTag,
            CommitSha = ctx.ResolvedCommitSha,
            TargetOs = ctx.TargetOs,
            TargetArch = ctx.TargetArch,
            SizeBytes = EngineBuildService.TryGetDirectorySize(installPath),
        };
    }

    // ==================== MODELS ====================

    private async Task<AgentJobResult> DownloadModelAsync(DownloadModelJob job, CancellationToken ct)
    {
        var hf = new HuggingFaceClient(CreateHttpClient(), new TokenSettings(_session), _loggerFactory.CreateLogger<HuggingFaceClient>());
        var shardNames = SplitGguf.AllShards(job.Filename);
        var shardPaths = SplitGguf.AllShards(job.DestinationPath);
        var reporter = new ByteProgress(this, job.JobId);
        string? resolvedSha = null;

        long? total = null;
        if (shardNames.Count > 1)
        {
            var sizes = (await hf.ListModelFilesAsync(job.RepoId, HfModelFormat.Gguf, ct))
                .ToDictionary(f => f.Filename, f => f.SizeBytes, StringComparer.OrdinalIgnoreCase);
            if (shardNames.All(n => sizes.GetValueOrDefault(n) is not null))
                total = shardNames.Sum(n => sizes[n]!.Value);
        }

        long done = 0;
        for (int i = 0; i < shardNames.Count; i++)
        {
            long offset = done;
            var progress = new Progress<DownloadProgress>(p => reporter.Report(offset + p.BytesReceived, total ?? p.TotalBytes));
            var sha = await hf.DownloadFileAsync(job.RepoId, shardNames[i], resolvedSha ?? job.Revision, shardPaths[i], progress, ct);
            resolvedSha ??= sha;
            done += new FileInfo(shardPaths[i]).Length;
        }
        reporter.Flush(done, total ?? done);

        var result = await ReadMetadataAsync(job.DestinationPath, ct);
        result.HfRevision = resolvedSha;
        return result;
    }

    private async Task<AgentJobResult> PullModelAsync(PullModelJob job, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(job.DestinationPath)!;
        Directory.CreateDirectory(directory);
        var targets = SplitGguf.AllShards(job.DestinationPath);
        if (targets.Count != job.Files.Count)
            targets = job.Files.Select(f => Path.Combine(directory, f)).ToList();

        using var http = CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Secret);
        http.DefaultRequestHeaders.Add(AgentProtocol.AgentIdHeader, _config.AgentId.ToString());

        var reporter = new ByteProgress(this, job.JobId);
        long done = 0;
        var buffer = new byte[1024 * 1024];
        for (int i = 0; i < job.Files.Count; i++)
        {
            var url = new Uri(new Uri(_config.RouterUrl), $"{AgentProtocol.ModelFilePath}/{job.SourceModelId}/{i}");
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var partial = targets[i] + ".part";
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(partial))
            {
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    reporter.Report(done, job.TotalBytes);
                }
            }
            File.Move(partial, targets[i], overwrite: true);
        }
        reporter.Flush(done, job.TotalBytes ?? done);

        return await ReadMetadataAsync(job.DestinationPath, ct);
    }

    private async Task<AgentJobResult> ReadMetadataAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"{filePath} doesn't exist on this machine.");

        var result = new AgentJobResult
        {
            FilePath = filePath,
            SizeBytes = SplitGguf.AllShards(filePath).Where(File.Exists).Sum(p => new FileInfo(p).Length),
        };
        if (filePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            var reader = new GgufMetadataReader();
            result.Metadata = await reader.ReadAsync(filePath, ct);
            result.DetectedMmprojPath = MmprojLocator.FindSiblingMmproj(filePath);
        }
        return result;
    }

    // ==================== FILES ====================

    private static async Task<AgentJobResult> DeleteAsync(DeletePathJob job, CancellationToken ct)
    {
        foreach (var path in job.Paths)
        {
            // A folder inside a WSL distro can't be deleted through its Windows network path (a venv's symlinks
            // stop it), and a Docker install has an image to remove as well.
            if (VllmLayout.ParseWslPath(path) is not null || VllmDocker.IsDockerFolder(path))
            {
                await VllmInstaller.DeleteAsync(path, ct);
                continue;
            }

            var full = Path.GetFullPath(path);
            if (Path.GetPathRoot(full) is { } root && string.Equals(root.TrimEnd('/', '\\'), full.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing to delete the filesystem root {full}.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            else if (File.Exists(full)) File.Delete(full);
        }
        return new AgentJobResult();
    }

    public static AgentPathStatus Stat(string path)
    {
        try
        {
            if (File.Exists(path))
                return new AgentPathStatus { Path = path, Exists = true, SizeBytes = new FileInfo(path).Length };
            if (Directory.Exists(path))
                return new AgentPathStatus { Path = path, Exists = true, SizeBytes = EngineBuildService.TryGetDirectorySize(path) };
        }
        catch { /* treat unreadable as missing */ }
        return new AgentPathStatus { Path = path, Exists = false };
    }

    private static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Throttles byte-count progress to a couple of messages a second.</summary>
    private sealed class ByteProgress
    {
        private readonly JobRunner _owner;
        private readonly Guid _jobId;
        private readonly Stopwatch _since = Stopwatch.StartNew();

        public ByteProgress(JobRunner owner, Guid jobId)
        {
            _owner = owner;
            _jobId = jobId;
        }

        public void Report(long received, long? total)
        {
            if (_since.Elapsed < ProgressInterval) return;
            _since.Restart();
            Flush(received, total);
        }

        public void Flush(long received, long? total) =>
            _ = _owner._session.SendAsync(new JobProgressMessage { JobId = _jobId, BytesReceived = received, TotalBytes = total });
    }

    /// <summary>Feeds the build pipeline's progress into job-progress messages.</summary>
    private sealed class ForwardingBuildPublisher : IEngineBuildProgressPublisher
    {
        private readonly AgentSession _session;
        public ForwardingBuildPublisher(AgentSession session) => _session = session;

        public Task PublishAsync(EngineBuildProgress progress)
        {
            // Completion/errors travel in the job's completion message instead.
            if (progress.Status != "running") return Task.CompletedTask;
            return _session.SendAsync(new JobProgressMessage
            {
                JobId = progress.BuildId,
                Phase = progress.Phase,
                Message = progress.Message,
                LogLine = progress.LogLine,
                Percent = progress.Percent,
            });
        }
    }

    /// <summary>Supplies the router's Hugging Face/GitHub tokens to the shared clients; the folder settings are unused on an agent.</summary>
    private sealed class TokenSettings : IModelLibrarySettingsService, IEngineBuildSettingsService
    {
        private readonly AgentSession _session;
        public TokenSettings(AgentSession session) => _session = session;

        Task<ModelLibrarySettings> IModelLibrarySettingsService.GetAsync(CancellationToken ct) =>
            Task.FromResult(new ModelLibrarySettings { HuggingFaceApiToken = _session.HuggingFaceToken });

        Task<EngineBuildSettings> IEngineBuildSettingsService.GetAsync(CancellationToken ct) =>
            Task.FromResult(new EngineBuildSettings { GitHubApiToken = _session.GitHubToken });

        Task IModelLibrarySettingsService.SaveAsync(string rootFolder, string? huggingFaceApiToken, CancellationToken ct) => Task.CompletedTask;

        Task IEngineBuildSettingsService.SaveAsync(string installRootFolder, string? buildWorkspaceFolder, string? gitHubApiToken, CancellationToken ct) => Task.CompletedTask;
    }
}
