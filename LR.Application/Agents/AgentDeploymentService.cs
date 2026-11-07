using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using LR.Core.Agents;
using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Core.Services.EngineBuilds;

namespace LR.Application.Agents;

/// <summary>
/// Everything the router deploys to agents: engine installs, updates and removals, model
/// downloads/copies/refreshes/removals, plus the bookkeeping when an agent (re)connects. Rows live
/// in the same tables as the router's own (<see cref="EngineBuild"/>, <see cref="LocalModel"/>) with
/// <c>AgentId</c> set, so the existing Engines/Models pages, progress hubs and build logs show
/// them too. A job's id is the row's id, so a result that arrives after a router restart still
/// lands on the right row.
/// </summary>
public sealed class AgentDeploymentService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentConnectionManager _connections;
    private readonly IEngineBuildProgressPublisher _buildProgress;
    private readonly IModelDownloadProgressPublisher _downloadProgress;
    private readonly EngineBuildService _buildService;
    private readonly IGitHubClient _github;
    private readonly ILogger<AgentDeploymentService> _logger;

    /// <summary>Router-side build logs for running remote engine jobs, so the Engines page's log view works.</summary>
    private readonly ConcurrentDictionary<Guid, BuildProgressSink> _sinks = new();

    /// <summary>Latest progress per running job, for the agent page's live view.</summary>
    private readonly ConcurrentDictionary<Guid, AgentJobProgress> _progress = new();

    /// <summary>In-place updates in flight — a failed one leaves the old install working.</summary>
    private readonly ConcurrentDictionary<Guid, bool> _inPlace = new();

    public AgentDeploymentService(
        IServiceScopeFactory scopeFactory,
        AgentConnectionManager connections,
        IEngineBuildProgressPublisher buildProgress,
        IModelDownloadProgressPublisher downloadProgress,
        EngineBuildService buildService,
        IGitHubClient github,
        ILogger<AgentDeploymentService> logger)
    {
        _scopeFactory = scopeFactory;
        _connections = connections;
        _buildProgress = buildProgress;
        _downloadProgress = downloadProgress;
        _buildService = buildService;
        _github = github;
        _logger = logger;
    }

    // ==================== PATH DEFAULTS ====================

    public static string EngineRootOf(RemoteAgent agent) =>
        !string.IsNullOrWhiteSpace(agent.EngineRoot) ? agent.EngineRoot!.Trim()
        : AgentPaths.Combine(agent.IsWindows, agent.DataDirectory ?? "", "engines");

    public static string ModelRootOf(RemoteAgent agent) =>
        !string.IsNullOrWhiteSpace(agent.ModelRoot) ? agent.ModelRoot!.Trim()
        : AgentPaths.Combine(agent.IsWindows, agent.DataDirectory ?? "", "models");

    private async Task<RemoteAgent> RequireOnlineAgentAsync(LRDbContext context, Guid agentId)
    {
        var agent = await context.RemoteAgents.FindAsync(agentId)
            ?? throw new InvalidOperationException("Agent not found.");
        if (!agent.IsEnabled)
            throw new InvalidOperationException($"Agent '{agent.Name}' is disabled.");
        if (!_connections.IsAgentOnline(agentId))
            throw new InvalidOperationException($"Agent '{agent.Name}' is offline.");
        if (string.IsNullOrWhiteSpace(agent.DataDirectory) && (string.IsNullOrWhiteSpace(agent.EngineRoot) || string.IsNullOrWhiteSpace(agent.ModelRoot)))
            throw new InvalidOperationException($"Agent '{agent.Name}' hasn't reported its data folder yet.");
        return agent;
    }

    // ==================== ENGINES ====================

    /// <summary>Installs a prebuilt llama.cpp / stable-diffusion.cpp release on the agent. Returns the new build's id.</summary>
    public async Task<Guid> StartEngineInstallAsync(Guid agentId, ServerEngine engine, BackendType backend, string? releaseTag, string? name)
    {
        var profile = EngineReleaseProfile.For(engine)
            ?? throw new InvalidOperationException($"{engine} can't be installed on agents.");

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var agent = await RequireOnlineAgentAsync(context, agentId);

        if (string.IsNullOrWhiteSpace(releaseTag))
        {
            var latest = await _github.GetLatestReleaseAsync(profile.Repo)
                ?? throw new InvalidOperationException("Could not reach GitHub to look up the latest release.");
            releaseTag = profile.VersionTagOf(latest.TagName);
        }
        releaseTag = releaseTag.Trim();

        var folder = AgentPaths.SafeSegment($"{profile.FolderPrefix}{releaseTag}-{backend.ToString().ToLowerInvariant()}");
        var outputDir = AgentPaths.Combine(agent.IsWindows, EngineRootOf(agent), folder);
        if (await context.EngineBuilds.AnyAsync(b => b.AgentId == agentId && b.InstallPath == outputDir))
            throw new InvalidOperationException($"'{agent.Name}' already has a build at {outputDir}. Delete it first or pick a different release.");

        var build = new EngineBuild
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"{profile.DisplayName} {releaseTag} ({backend})" : name.Trim(),
            Engine = engine,
            BackendType = backend,
            Source = EngineBuildSource.OfficialRelease,
            InstallPath = outputDir,
            VersionTag = releaseTag,
            Status = EngineBuildStatus.Downloading,
            StatusMessage = $"Installing on {agent.Name}…",
            AgentId = agentId,
        };
        context.EngineBuilds.Add(build);
        await context.SaveChangesAsync();

        await SendJobAsync(agentId, build.Id, new InstallEngineJob
        {
            JobId = build.Id,
            Engine = engine,
            Backend = backend,
            ReleaseTag = releaseTag,
            OutputDir = outputDir,
        }, isEngine: true);
        _connections.Log(agentId, "info", $"Installing {build.Name}.");
        return build.Id;
    }

    /// <summary>Compiles an engine from a recipe on the agent (it needs git, cmake and a toolchain).</summary>
    public async Task<Guid> StartSourceBuildAsync(Guid agentId, Guid recipeId, string? gitRef, string? name)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var agent = await RequireOnlineAgentAsync(context, agentId);
        var recipe = await context.LlamaCppBuildRecipes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recipeId)
            ?? throw new InvalidOperationException("Recipe not found.");
        var profile = EngineReleaseProfile.For(recipe.Engine)
            ?? throw new InvalidOperationException($"{recipe.Engine} can't be compiled from a recipe.");

        var refLabel = string.IsNullOrWhiteSpace(gitRef) ? recipe.GitRef : gitRef.Trim();
        var slug = AgentPaths.SafeSegment(recipe.Name).Replace(' ', '-').ToLowerInvariant();
        var folder = AgentPaths.SafeSegment($"{profile.FolderPrefix}{refLabel}-{recipe.BackendType.ToString().ToLowerInvariant()}-{slug}");
        var outputDir = AgentPaths.Combine(agent.IsWindows, EngineRootOf(agent), folder);
        if (await context.EngineBuilds.AnyAsync(b => b.AgentId == agentId && b.InstallPath == outputDir))
            throw new InvalidOperationException($"'{agent.Name}' already has a build at {outputDir}. Delete it or change the recipe/ref.");

        var build = new EngineBuild
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"{recipe.Name} ({refLabel})" : name.Trim(),
            Engine = recipe.Engine,
            BackendType = recipe.BackendType,
            Source = EngineBuildSource.SourceCompile,
            RecipeId = recipe.Id,
            InstallPath = outputDir,
            Status = EngineBuildStatus.Building,
            StatusMessage = $"Compiling on {agent.Name}…",
            AgentId = agentId,
        };
        context.EngineBuilds.Add(build);
        await context.SaveChangesAsync();

        await SendJobAsync(agentId, build.Id, new InstallEngineJob
        {
            JobId = build.Id,
            Engine = recipe.Engine,
            Backend = recipe.BackendType,
            Recipe = recipe,
            GitRef = string.IsNullOrWhiteSpace(gitRef) ? null : gitRef.Trim(),
            OutputDir = outputDir,
        }, isEngine: true);
        _connections.Log(agentId, "info", $"Compiling {build.Name}.");
        return build.Id;
    }

    /// <summary>Updates an agent's install in place (re-download or recompile), like a local in-place update.</summary>
    public async Task<Guid> StartEngineUpdateAsync(Guid buildId, string? refOverride)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var build = await context.EngineBuilds.FindAsync(buildId)
            ?? throw new InvalidOperationException("Build not found.");
        if (build.AgentId is not { } agentId)
            throw new InvalidOperationException("Not a remote build.");
        await RequireOnlineAgentAsync(context, agentId);
        if (build.Status is EngineBuildStatus.Downloading or EngineBuildStatus.Building or EngineBuildStatus.Pending)
            throw new InvalidOperationException("This build is already running.");

        var busy = await context.BackendConfigs
            .Where(c => c.EngineBuildId == build.Id)
            .Join(context.ServerInstances, c => c.ServerInstanceId, s => s.Id, (c, s) => s.Status)
            .CountAsync(st => st == ServerStatus.Running || st == ServerStatus.Starting || st == ServerStatus.Reconnecting);
        if (busy > 0)
            throw new InvalidOperationException($"Stop the {busy} running server(s) using this install before updating it in place.");

        LlamaCppBuildRecipe? recipe = null;
        if (build.Source == EngineBuildSource.SourceCompile)
        {
            recipe = build.RecipeId is { } rid ? await context.LlamaCppBuildRecipes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid) : null;
            if (recipe is null)
                throw new InvalidOperationException("The recipe this build was compiled from no longer exists — rebuild it as a new build instead.");
        }

        build.Status = build.Source == EngineBuildSource.SourceCompile ? EngineBuildStatus.Building : EngineBuildStatus.Downloading;
        build.StatusMessage = "Updating in place…";
        await context.SaveChangesAsync();

        _inPlace[build.Id] = true;
        await SendJobAsync(agentId, build.Id, new InstallEngineJob
        {
            JobId = build.Id,
            Engine = build.Engine,
            Backend = build.BackendType,
            ReleaseTag = recipe is null && !string.IsNullOrWhiteSpace(refOverride) ? refOverride.Trim() : null,
            Recipe = recipe,
            GitRef = recipe is not null && !string.IsNullOrWhiteSpace(refOverride) ? refOverride.Trim() : null,
            OutputDir = build.InstallPath,
            InPlace = true,
        }, isEngine: true);
        _connections.Log(agentId, "info", $"Updating {build.Name} in place.");
        return build.Id;
    }

    /// <summary>Unregisters an agent's install, and deletes its folder on the agent when asked (and it's online).</summary>
    public async Task<bool> DeleteBuildAsync(Guid buildId, bool deleteFiles)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var build = await context.EngineBuilds.AsNoTracking().FirstOrDefaultAsync(b => b.Id == buildId);
        if (build?.AgentId is not { } agentId) return false;

        if (deleteFiles && !_connections.IsAgentOnline(agentId))
            throw new InvalidOperationException("The agent is offline — its files can't be deleted right now. Delete without removing files, or try again when it's back.");

        var manager = scope.ServiceProvider.GetRequiredService<IEngineBuildManager>();
        if (!await manager.DeleteBuildAsync(buildId, deleteFiles: false)) return false;

        if (deleteFiles && !string.IsNullOrWhiteSpace(build.InstallPath))
        {
            await _connections.SendAsync(agentId, new DeletePathJob { JobId = Guid.NewGuid(), Paths = [build.InstallPath] });
            _connections.Log(agentId, "info", $"Deleting {build.InstallPath}.");
        }
        return true;
    }

    public async Task<bool> CancelAsync(Guid jobId)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var agentId = await context.EngineBuilds.Where(b => b.Id == jobId).Select(b => b.AgentId).FirstOrDefaultAsync()
            ?? await context.LocalModels.Where(m => m.Id == jobId).Select(m => m.AgentId).FirstOrDefaultAsync();
        if (agentId is null || !_connections.IsAgentOnline(agentId.Value)) return false;
        await _connections.SendAsync(agentId.Value, new CancelJobMessage { JobId = jobId });
        return true;
    }

    // ==================== MODELS ====================

    /// <summary>Downloads a Hugging Face file straight onto the agent. Returns the new library entry's id.</summary>
    public async Task<Guid> StartModelDownloadAsync(Guid agentId, string repoId, string filename, string? revision, string? name)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var agent = await RequireOnlineAgentAsync(context, agentId);

        repoId = repoId.Trim();
        filename = filename.Trim();
        var destination = AgentPaths.Combine(agent.IsWindows, ModelRootOf(agent), AgentPaths.SafeSegment(repoId.Replace('/', '_')), filename);
        if (await context.LocalModels.AnyAsync(m => m.AgentId == agentId && m.FilePath == destination))
            throw new InvalidOperationException($"'{agent.Name}' already has {destination} in its library.");

        var model = new LocalModel
        {
            Name = string.IsNullOrWhiteSpace(name) ? ModelDownloadService.DefaultName(repoId, filename) : name.Trim(),
            FilePath = destination,
            Source = ModelSource.HuggingFace,
            Status = ModelStatus.Downloading,
            HfRepoId = repoId,
            HfFilename = filename,
            HfRevision = string.IsNullOrWhiteSpace(revision) ? "main" : revision.Trim(),
            AgentId = agentId,
        };
        context.LocalModels.Add(model);
        await context.SaveChangesAsync();

        await SendJobAsync(agentId, model.Id, new DownloadModelJob
        {
            JobId = model.Id,
            RepoId = repoId,
            Filename = filename,
            Revision = model.HfRevision,
            DestinationPath = destination,
        }, isEngine: false);
        _connections.Log(agentId, "info", $"Downloading {repoId}/{filename}.");
        return model.Id;
    }

    /// <summary>Re-downloads an agent's Hugging Face model at the repo's latest revision, in place.</summary>
    public async Task<Guid> StartModelUpdateAsync(Guid modelId)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var model = await context.LocalModels.FindAsync(modelId) ?? throw new InvalidOperationException("Model not found.");
        if (model.AgentId is not { } agentId) throw new InvalidOperationException("Not a remote model.");
        if (model.Source != ModelSource.HuggingFace || string.IsNullOrEmpty(model.HfRepoId) || string.IsNullOrEmpty(model.HfFilename))
            throw new InvalidOperationException("Not a Hugging Face model.");
        if (model.Status == ModelStatus.Downloading)
            throw new InvalidOperationException("Already downloading.");
        await RequireOnlineAgentAsync(context, agentId);

        model.Status = ModelStatus.Downloading;
        model.StatusMessage = null;
        await context.SaveChangesAsync();
        await SendJobAsync(agentId, model.Id, new DownloadModelJob
        {
            JobId = model.Id,
            RepoId = model.HfRepoId,
            Filename = model.HfFilename,
            Revision = "main",
            DestinationPath = model.FilePath,
        }, isEngine: false);
        return model.Id;
    }

    /// <summary>Copies a model from this router's library to the agent. Returns the agent's new library entry.</summary>
    public async Task<Guid> StartModelPullAsync(Guid agentId, Guid sourceModelId, string? name)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var agent = await RequireOnlineAgentAsync(context, agentId);
        var source = await context.LocalModels.AsNoTracking().FirstOrDefaultAsync(m => m.Id == sourceModelId)
            ?? throw new InvalidOperationException("Source model not found.");
        if (source.AgentId is not null)
            throw new InvalidOperationException("Only models on the router's own machine can be copied to an agent.");

        var shards = SplitGguf.AllShards(source.FilePath);
        if (shards.FirstOrDefault(p => !File.Exists(p)) is { } missing)
            throw new InvalidOperationException($"{missing} is missing on the router.");

        var subfolder = !string.IsNullOrEmpty(source.HfRepoId)
            ? AgentPaths.SafeSegment(source.HfRepoId.Replace('/', '_'))
            : "library";
        var fileName = Path.GetFileName(source.FilePath);
        var destination = AgentPaths.Combine(agent.IsWindows, ModelRootOf(agent), subfolder, fileName);
        if (await context.LocalModels.AnyAsync(m => m.AgentId == agentId && m.FilePath == destination))
            throw new InvalidOperationException($"'{agent.Name}' already has {destination} in its library.");

        var model = new LocalModel
        {
            Name = string.IsNullOrWhiteSpace(name) ? source.Name : name.Trim(),
            FilePath = destination,
            Source = source.Source,
            Status = ModelStatus.Downloading,
            HfRepoId = source.HfRepoId,
            HfFilename = source.HfFilename,
            HfRevision = source.HfRevision,
            Notes = source.Notes,
            AgentId = agentId,
        };
        context.LocalModels.Add(model);
        await context.SaveChangesAsync();

        await SendJobAsync(agentId, model.Id, new PullModelJob
        {
            JobId = model.Id,
            SourceModelId = source.Id,
            Files = shards.Select(Path.GetFileName).ToList()!,
            TotalBytes = shards.Sum(p => new FileInfo(p).Length),
            DestinationPath = destination,
        }, isEngine: false);
        _connections.Log(agentId, "info", $"Copying {source.Name} from the router.");
        return model.Id;
    }

    /// <summary>Registers a model file already on the agent's disk.</summary>
    public async Task<Guid> ImportModelAsync(Guid agentId, string filePath, string? name)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var agent = await RequireOnlineAgentAsync(context, agentId);
        filePath = filePath.Trim();
        if (await context.LocalModels.AnyAsync(m => m.AgentId == agentId && m.FilePath == filePath))
            throw new InvalidOperationException($"'{agent.Name}' already has {filePath} in its library.");

        var model = new LocalModel
        {
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(AgentPaths.FileName(filePath)) : name.Trim(),
            FilePath = filePath,
            Source = ModelSource.Local,
            Status = ModelStatus.Downloading,
            StatusMessage = "Reading metadata on the agent…",
            AgentId = agentId,
        };
        context.LocalModels.Add(model);
        await context.SaveChangesAsync();
        await SendJobAsync(agentId, model.Id, new ReadModelMetadataJob { JobId = model.Id, FilePath = filePath }, isEngine: false);
        return model.Id;
    }

    /// <summary>Re-reads an agent model's metadata on the agent.</summary>
    public async Task RefreshModelAsync(Guid modelId)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var model = await context.LocalModels.AsNoTracking().FirstOrDefaultAsync(m => m.Id == modelId)
            ?? throw new InvalidOperationException("Model not found.");
        if (model.AgentId is not { } agentId) throw new InvalidOperationException("Not a remote model.");
        await RequireOnlineAgentAsync(context, agentId);
        await SendJobAsync(agentId, model.Id, new ReadModelMetadataJob { JobId = model.Id, FilePath = model.FilePath }, isEngine: false);
    }

    public async Task<bool> DeleteModelAsync(Guid modelId, bool deleteFile)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var model = await context.LocalModels.AsNoTracking().FirstOrDefaultAsync(m => m.Id == modelId);
        if (model?.AgentId is not { } agentId) return false;
        if (deleteFile && !_connections.IsAgentOnline(agentId))
            throw new InvalidOperationException("The agent is offline — its files can't be deleted right now. Delete without removing the file, or try again when it's back.");

        var library = scope.ServiceProvider.GetRequiredService<IModelLibrary>();
        if (!await library.DeleteAsync(modelId, deleteFile: false)) return false;

        if (deleteFile)
        {
            await _connections.SendAsync(agentId, new DeletePathJob { JobId = Guid.NewGuid(), Paths = SplitGguf.AllShards(model.FilePath).ToList() });
            _connections.Log(agentId, "info", $"Deleting {model.FilePath}.");
        }
        return true;
    }

    // ==================== JOB TRAFFIC ====================

    private async Task SendJobAsync(Guid agentId, Guid rowId, AgentJobMessage job, bool isEngine)
    {
        try
        {
            if (isEngine)
                await GetSinkAsync(rowId);
            await _connections.SendAsync(agentId, job);
        }
        catch (Exception ex)
        {
            await FailRowAsync(rowId, $"Couldn't send the job to the agent: {ex.Message}");
            throw;
        }
    }

    private async Task<BuildProgressSink> GetSinkAsync(Guid buildId)
    {
        if (_sinks.TryGetValue(buildId, out var sink)) return sink;
        var workRoot = await _buildService.GetWorkRootAsync(buildId) ?? Path.Combine(AppContext.BaseDirectory, "data", "engine-builds", ".work", buildId.ToString("N"));
        sink = new BuildProgressSink(buildId, _buildProgress, Path.Combine(workRoot, "build.log"), _logger);
        return _sinks.GetOrAdd(buildId, sink);
    }

    /// <summary>The latest progress of a running job, if any has arrived.</summary>
    public AgentJobProgress? GetProgress(Guid jobId) => _progress.TryGetValue(jobId, out var p) ? p : null;

    public async Task OnJobProgressAsync(Guid agentId, JobProgressMessage progress)
    {
        var previous = _progress.GetValueOrDefault(progress.JobId);
        double? percent = progress.BytesReceived is { } got && progress.TotalBytes is > 0
            ? Math.Round(got * 100.0 / progress.TotalBytes.Value, 1)
            : progress.Percent ?? previous?.Percent;
        _progress[progress.JobId] = new AgentJobProgress(
            progress.Message ?? (progress.BytesReceived is { } b ? $"{b / 1048576.0:F0} MB{(progress.TotalBytes is > 0 ? $" of {progress.TotalBytes.Value / 1048576.0:F0} MB" : "")}" : previous?.Message),
            progress.LogLine ?? previous?.LastLine,
            percent,
            DateTime.UtcNow);

        if (progress.BytesReceived is not null)
        {
            // Model transfer.
            await _downloadProgress.PublishAsync(new DownloadProgress
            {
                ModelId = progress.JobId,
                BytesReceived = progress.BytesReceived.Value,
                TotalBytes = progress.TotalBytes,
            });
            return;
        }

        var sink = await GetSinkAsync(progress.JobId);
        var phase = progress.Phase ?? "remote";
        if (progress.LogLine is not null)
            await sink.LineAsync(phase, progress.LogLine);
        else if (progress.Message is not null)
            await sink.PhaseAsync(phase, progress.Message, progress.Percent);
        else
            await _buildProgress.PublishAsync(new EngineBuildProgress { BuildId = progress.JobId, Phase = phase, Percent = progress.Percent, Status = "running" });
    }

    public async Task OnJobCompletedAsync(Guid agentId, JobCompletedMessage done)
    {
        _progress.TryRemove(done.JobId, out _);
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();

        if (await context.EngineBuilds.FirstOrDefaultAsync(b => b.Id == done.JobId && b.AgentId == agentId) is { } build)
        {
            await CompleteBuildAsync(context, build, done);
            return;
        }

        if (await context.LocalModels.FirstOrDefaultAsync(m => m.Id == done.JobId && m.AgentId == agentId) is { } model)
        {
            await CompleteModelAsync(context, model, done);
            return;
        }

        // Deletes and anything whose row is gone.
        if (!done.Success && !done.Cancelled)
            _connections.Log(agentId, "error", $"Job failed: {done.Error}");
    }

    private async Task CompleteBuildAsync(LRDbContext context, EngineBuild build, JobCompletedMessage done)
    {
        bool inPlace = _inPlace.TryRemove(build.Id, out _) || build.StatusMessage == "Updating in place…";
        _sinks.TryRemove(build.Id, out var sink);
        sink ??= await GetSinkAsync(build.Id);
        _sinks.TryRemove(build.Id, out _);

        if (done.Success)
        {
            var r = done.Result;
            build.Status = EngineBuildStatus.Ready;
            build.StatusMessage = null;
            build.InstallPath = r.InstallPath ?? build.InstallPath;
            build.VersionTag = r.VersionTag ?? build.VersionTag;
            build.CommitSha = r.CommitSha ?? build.CommitSha;
            build.TargetOs = r.TargetOs ?? build.TargetOs;
            build.TargetArch = r.TargetArch ?? build.TargetArch;
            build.SizeBytes = r.SizeBytes;
            build.BuildCompletedAt = DateTime.UtcNow;
            if (inPlace && build.Source == EngineBuildSource.OfficialRelease && r.VersionTag is not null
                && EngineReleaseProfile.For(build.Engine) is { } profile)
                build.Name = $"{profile.DisplayName} {r.VersionTag} ({build.BackendType})";
            await context.SaveChangesAsync();
            await sink.CompletedAsync(inPlace ? $"Updated in place to {r.VersionTag ?? "the latest version"}." : "Installed.");
            if (build.AgentId is { } aid) _connections.Log(aid, "info", $"{build.Name} is ready.");
            return;
        }

        if (inPlace && build.BuildCompletedAt is not null)
        {
            build.Status = EngineBuildStatus.Ready;
            build.StatusMessage = done.Cancelled
                ? "Update cancelled — the existing install was left untouched."
                : $"Last update attempt failed (existing install kept): {done.Error}";
        }
        else
        {
            build.Status = EngineBuildStatus.Error;
            build.StatusMessage = done.Error ?? "Failed.";
        }
        await context.SaveChangesAsync();
        await sink.ErrorAsync(done.Error ?? "Failed.");
        if (build.AgentId is { } agentId) _connections.Log(agentId, "error", $"{build.Name}: {done.Error}");
    }

    private async Task CompleteModelAsync(LRDbContext context, LocalModel model, JobCompletedMessage done)
    {
        if (done.Success)
        {
            var r = done.Result;
            model.FilePath = r.FilePath ?? model.FilePath;
            model.FileSizeBytes = r.SizeBytes ?? model.FileSizeBytes;
            if (!string.IsNullOrEmpty(r.HfRevision)) model.HfRevision = r.HfRevision;
            if (r.Metadata is not null)
                ModelLibraryManager.ApplyGgufMetadata(model, r.Metadata, r.DetectedMmprojPath);
            model.Status = ModelStatus.Ready;
            model.StatusMessage = null;
            model.LastVerifiedAt = DateTimeOffset.UtcNow;
            model.UpdatedAt = DateTimeOffset.UtcNow;

            foreach (var preset in await context.ModelPresets.Where(p => p.ModelId == model.Id).ToListAsync())
                PresetGgufSync.ApplyFromModel(preset, model);

            await context.SaveChangesAsync();
            await _downloadProgress.PublishAsync(new DownloadProgress { ModelId = model.Id, Status = "completed" });
            return;
        }

        model.Status = done.Error?.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase) == true ? ModelStatus.Missing : ModelStatus.Error;
        model.StatusMessage = done.Cancelled ? "Download cancelled." : done.Error;
        await context.SaveChangesAsync();
        await _downloadProgress.PublishAsync(new DownloadProgress { ModelId = model.Id, Status = "error", ErrorMessage = done.Error });
    }

    private async Task FailRowAsync(Guid rowId, string message)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        if (await context.EngineBuilds.FindAsync(rowId) is { } build)
        {
            build.Status = EngineBuildStatus.Error;
            build.StatusMessage = message;
        }
        else if (await context.LocalModels.FindAsync(rowId) is { } model)
        {
            model.Status = ModelStatus.Error;
            model.StatusMessage = message;
        }
        await context.SaveChangesAsync();
    }

    // ==================== RECONNECT BOOKKEEPING ====================

    /// <summary>
    /// After an agent says hello: re-attach to its servers that kept running, settle servers that
    /// didn't, mark jobs that died with a previous agent process, and check its engines/models are
    /// still on disk.
    /// </summary>
    public async Task OnAgentConnectedAsync(Guid agentId, AgentHelloMessage hello, CancellationToken ct)
    {
        try
        {
            await ReattachServersAsync(agentId, hello, ct);
            await SettleInterruptedJobsAsync(agentId, hello, ct);
            await ReconcileFilesAsync(agentId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconciling agent {AgentId} after it connected failed.", agentId);
            _connections.Log(agentId, "error", $"Reconciliation failed: {ex.Message}");
        }
    }

    private async Task ReattachServersAsync(Guid agentId, AgentHelloMessage hello, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var serverManager = scope.ServiceProvider.GetRequiredService<IServerManager>();
        var servers = await context.ServerInstances.AsNoTracking().Where(s => s.AgentId == agentId).ToListAsync(ct);
        var alive = hello.Wrappers.ToDictionary(w => w.InstanceId);

        foreach (var server in servers)
        {
            bool hasWrapper = alive.ContainsKey(server.Id);
            bool believedUp = server.Status is ServerStatus.Running or ServerStatus.Starting or ServerStatus.Reconnecting;
            if (!hasWrapper && !believedUp) continue;
            if (server.Status == ServerStatus.Starting) continue; // a start in flight owns its own outcome

            var reattached = await serverManager.TryReattachAsync(server.Id, ct);
            _connections.Log(agentId, reattached ? "info" : "warning", reattached
                ? $"Re-attached to server '{server.Name}'."
                : $"Server '{server.Name}' was not running on the agent.");
        }

        // Wrappers for servers that no longer exist here (deleted while the agent was away).
        var known = servers.Select(s => s.Id).ToHashSet();
        foreach (var orphan in hello.Wrappers.Where(w => !known.Contains(w.InstanceId)))
        {
            try
            {
                if (await _connections.AttachAsync(agentId, orphan.InstanceId, launch: false, ct) is { } transport)
                {
                    await transport.ReceiveAsync(ct); // hello
                    await transport.SendAsync(new LR.Core.Wrapper.StopCommand { StopCompanion = true, ShutdownWrapper = true }, ct);
                    await transport.DisposeAsync();
                    _connections.Log(agentId, "info", $"Stopped an orphaned wrapper ({orphan.InstanceId}).");
                }
            }
            catch (Exception ex)
            {
                _connections.Log(agentId, "warning", $"Couldn't stop orphaned wrapper {orphan.InstanceId}: {ex.Message}");
            }
        }
    }

    private async Task SettleInterruptedJobsAsync(Guid agentId, AgentHelloMessage hello, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var active = hello.ActiveJobs.ToHashSet();
        var changed = false;

        foreach (var build in await context.EngineBuilds
                     .Where(b => b.AgentId == agentId && (b.Status == EngineBuildStatus.Downloading || b.Status == EngineBuildStatus.Building || b.Status == EngineBuildStatus.Pending))
                     .ToListAsync(ct))
        {
            if (active.Contains(build.Id)) continue;
            // Give an undelivered completion (replayed right after the welcome) a moment to land first.
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            await context.Entry(build).ReloadAsync(ct);
            if (build.Status is not (EngineBuildStatus.Downloading or EngineBuildStatus.Building or EngineBuildStatus.Pending)) continue;
            build.Status = build.BuildCompletedAt is not null ? EngineBuildStatus.Ready : EngineBuildStatus.Error;
            build.StatusMessage = "Interrupted — the agent restarted while this was running.";
            changed = true;
        }

        foreach (var model in await context.LocalModels.Where(m => m.AgentId == agentId && m.Status == ModelStatus.Downloading).ToListAsync(ct))
        {
            if (active.Contains(model.Id)) continue;
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            await context.Entry(model).ReloadAsync(ct);
            if (model.Status != ModelStatus.Downloading) continue;
            model.Status = ModelStatus.Error;
            model.StatusMessage = "Interrupted — the agent restarted while this was running.";
            changed = true;
        }

        if (changed) await context.SaveChangesAsync(ct);
    }

    /// <summary>Marks the agent's engines/models whose files have vanished as Missing (and back to Ready when they reappear).</summary>
    public async Task ReconcileFilesAsync(Guid agentId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var builds = await context.EngineBuilds
            .Where(b => b.AgentId == agentId && (b.Status == EngineBuildStatus.Ready || b.Status == EngineBuildStatus.Missing))
            .ToListAsync(ct);
        var models = await context.LocalModels
            .Where(m => m.AgentId == agentId && (m.Status == ModelStatus.Ready || m.Status == ModelStatus.Missing))
            .ToListAsync(ct);

        var paths = builds.Select(b => b.InstallPath).Concat(models.Select(m => m.FilePath))
            .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        var report = await _connections.CheckPathsAsync(agentId, paths, ct);
        if (report is null) return;
        var exists = report.ToDictionary(p => p.Path, p => p);

        foreach (var build in builds)
        {
            var present = exists.TryGetValue(build.InstallPath, out var s) && s.Exists;
            build.Status = present ? EngineBuildStatus.Ready : EngineBuildStatus.Missing;
            if (present && s!.SizeBytes is { } size) build.SizeBytes = size;
            if (!present) build.StatusMessage = "The install folder is no longer on the agent.";
            else if (build.StatusMessage == "The install folder is no longer on the agent.") build.StatusMessage = null;
        }
        foreach (var model in models)
        {
            var present = exists.TryGetValue(model.FilePath, out var s) && s.Exists;
            model.Status = present ? ModelStatus.Ready : ModelStatus.Missing;
            model.StatusMessage = present ? null : "File not found on the agent.";
            if (present) model.LastVerifiedAt = DateTimeOffset.UtcNow;
        }
        await context.SaveChangesAsync(ct);
    }
}

/// <summary>Latest known progress of a job running on an agent.</summary>
public sealed record AgentJobProgress(string? Message, string? LastLine, double? Percent, DateTime At);
