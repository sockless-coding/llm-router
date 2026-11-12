using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Services;

/// <summary>What one <see cref="SharedLibraryDiscovery"/> pass found.</summary>
public sealed record DiscoveryResult(int EnginesAdded, int ModelsAdded, int ModelsLinked, int ModelsMissing, int ModelsRestored)
{
    public string Summary()
    {
        var parts = new List<string>();
        if (EnginesAdded > 0) parts.Add($"{EnginesAdded} engine install(s) added");
        if (ModelsAdded > 0) parts.Add($"{ModelsAdded} model(s) added");
        if (ModelsLinked > 0) parts.Add($"{ModelsLinked} model(s) linked to Hugging Face");
        if (ModelsMissing > 0) parts.Add($"{ModelsMissing} model(s) no longer on disk");
        if (ModelsRestored > 0) parts.Add($"{ModelsRestored} model(s) back on disk");
        return parts.Count == 0 ? "Nothing new found." : string.Join(", ", parts) + ".";
    }
}

/// <summary>
/// Keeps this instance's registries in step with the engine install root and model library root, for
/// setups where several router instances share those folders: registers installs and model files another
/// instance put there, links model files to the Hugging Face repo they came from (so updates can be
/// checked), and picks up in-place updates and deletions made elsewhere. Only touches this machine's
/// entries (never a remote agent's). Run by the background sync service and the pages' "Scan now".
/// </summary>
public class SharedLibraryDiscovery
{
    /// <summary>One pass at a time across the process (the background service and a "Scan now" click).</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Model paths whose Hugging Face repo was looked up and not found, and when — so the Hub isn't asked every pass.</summary>
    private static readonly ConcurrentDictionary<string, DateTime> FailedHubLookups = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan HubLookupRetry = TimeSpan.FromHours(12);

    private readonly LRDbContext _context;
    private readonly IModelLibrary _library;
    private readonly IModelLibrarySettingsService _modelSettings;
    private readonly IEngineBuildSettingsService _engineSettings;
    private readonly IEngineBuildManager _engineManager;
    private readonly IHuggingFaceClient _hf;
    private readonly Dictionary<ServerEngine, IEngineInstallHandler> _handlers;
    private readonly ILogger<SharedLibraryDiscovery> _logger;

    public SharedLibraryDiscovery(
        LRDbContext context,
        IModelLibrary library,
        IModelLibrarySettingsService modelSettings,
        IEngineBuildSettingsService engineSettings,
        IEngineBuildManager engineManager,
        IHuggingFaceClient hf,
        IEnumerable<IEngineInstallHandler> handlers,
        ILogger<SharedLibraryDiscovery> logger)
    {
        _context = context;
        _library = library;
        _modelSettings = modelSettings;
        _engineSettings = engineSettings;
        _engineManager = engineManager;
        _hf = hf;
        _handlers = handlers.ToDictionary(h => h.Engine);
        _logger = logger;
    }

    /// <summary>
    /// Runs a full pass: engines, then models. <paramref name="retryHubLookups"/> asks the Hub again about
    /// files it recently couldn't match (a manual scan); the background pass leaves them for a while.
    /// </summary>
    public async Task<DiscoveryResult> RunAsync(bool retryHubLookups = false, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await PruneExclusionsAsync(ct);
            var engines = await DiscoverEnginesAsync(ct);
            await _engineManager.ReconcileAsync(ct);
            var (added, missing, restored) = await DiscoverModelsAsync(ct);
            var linked = await LinkHuggingFaceAsync(retryHubLookups, ct);
            return new DiscoveryResult(engines, added, linked, missing, restored);
        }
        finally
        {
            Gate.Release();
        }
    }

    // ==================== EXCLUSIONS ====================

    public static string NormalizePath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }

    /// <summary>Keeps a removed entry's path from being registered again by discovery. No-op if already excluded.</summary>
    public static async Task ExcludeAsync(LRDbContext context, DiscoveryExclusionKind kind, string path, CancellationToken ct = default)
    {
        var normalized = NormalizePath(path);
        var existing = await context.DiscoveryExclusions.Where(e => e.Kind == kind).Select(e => e.Path).ToListAsync(ct);
        if (existing.Contains(normalized, StringComparer.OrdinalIgnoreCase)) return;
        context.DiscoveryExclusions.Add(new DiscoveryExclusion { Kind = kind, Path = normalized });
        await context.SaveChangesAsync(ct);
    }

    /// <summary>Lets discovery register <paramref name="path"/> again (it was imported by hand).</summary>
    public static async Task ClearExclusionAsync(LRDbContext context, DiscoveryExclusionKind kind, string path, CancellationToken ct = default)
    {
        var normalized = NormalizePath(path);
        var rows = (await context.DiscoveryExclusions.Where(e => e.Kind == kind).ToListAsync(ct))
            .Where(e => string.Equals(e.Path, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.Count == 0) return;
        context.DiscoveryExclusions.RemoveRange(rows);
        await context.SaveChangesAsync(ct);
    }

    /// <summary>An exclusion whose files are gone has done its job; a later download to the same path should be picked up.</summary>
    private async Task PruneExclusionsAsync(CancellationToken ct)
    {
        var gone = (await _context.DiscoveryExclusions.ToListAsync(ct))
            .Where(e => !File.Exists(e.Path) && !Directory.Exists(e.Path))
            .ToList();
        if (gone.Count == 0) return;
        _context.DiscoveryExclusions.RemoveRange(gone);
        await _context.SaveChangesAsync(ct);
    }

    private async Task<HashSet<string>> ExcludedAsync(DiscoveryExclusionKind kind, CancellationToken ct) =>
        new((await _context.DiscoveryExclusions.Where(e => e.Kind == kind).Select(e => e.Path).ToListAsync(ct)),
            StringComparer.OrdinalIgnoreCase);

    // ==================== ENGINES ====================

    /// <summary>Registers install folders under the install root that this instance doesn't track yet. Returns how many.</summary>
    public async Task<int> DiscoverEnginesAsync(CancellationToken ct = default)
    {
        var settings = await _engineSettings.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.InstallRootFolder) || !Directory.Exists(settings.InstallRootFolder))
            return 0;

        var root = NormalizePath(settings.InstallRootFolder);
        var workspace = NormalizePath(settings.ResolveWorkspaceRoot());
        var known = new HashSet<string>(
            (await _context.EngineBuilds.Where(b => b.AgentId == null).Select(b => b.InstallPath).ToListAsync(ct)).Select(NormalizePath),
            StringComparer.OrdinalIgnoreCase);
        var excluded = await ExcludedAsync(DiscoveryExclusionKind.Engine, ct);
        var recipes = await _context.LlamaCppBuildRecipes.ToListAsync(ct);

        IEnumerable<string> folders;
        try { folders = Directory.EnumerateDirectories(root).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not list the engine install root {Root}.", root);
            return 0;
        }

        var added = 0;
        foreach (var dir in folders)
        {
            ct.ThrowIfCancellationRequested();
            var path = NormalizePath(dir);
            var folderName = Path.GetFileName(path);
            if (folderName.StartsWith('.') || known.Contains(path) || excluded.Contains(path) ||
                string.Equals(path, workspace, StringComparison.OrdinalIgnoreCase))
                continue;

            // A Docker install's image lives in that machine's Docker, and only one may exist per engine.
            if (TensorFoldDocker.IsDockerFolder(path) || VllmDocker.IsDockerFolder(path))
                continue;

            try
            {
                var build = DetectInstall(path, folderName, recipes);
                if (build is null) continue;

                _context.EngineBuilds.Add(build);
                await _context.SaveChangesAsync(ct);
                known.Add(path);
                added++;
                _logger.LogInformation("Registered engine install {Path} ({Engine} {Version}) found in the shared install root.",
                    path, build.Engine, build.VersionTag ?? "unknown version");

                if (EngineInstallManifest.TryRead(path) is null)
                    EngineInstallManifest.FromBuild(build, recipes.FirstOrDefault(r => r.Id == build.RecipeId)?.Name).TryWrite(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not register engine install {Path}.", path);
            }
        }
        return added;
    }

    /// <summary>A new registry row for the install in <paramref name="path"/>, or null when it isn't one this machine can use.</summary>
    private EngineBuild? DetectInstall(string path, string folderName, IReadOnlyList<LlamaCppBuildRecipe> recipes)
    {
        EngineBuild build;
        IEngineInstallHandler? handler;

        if (EngineInstallManifest.TryRead(path) is { } manifest)
        {
            handler = _handlers.GetValueOrDefault(manifest.Engine);
            if (handler is null || !manifest.RunsOnThisHost() || !handler.IsInstallPresent(path))
                return null;
            var recipeId = manifest.RecipeName is { } recipeName
                ? recipes.FirstOrDefault(r => r.Engine == manifest.Engine && string.Equals(r.Name, recipeName, StringComparison.OrdinalIgnoreCase))?.Id
                : null;
            build = manifest.ToBuild(path, recipeId);
        }
        else
        {
            handler = _handlers.Values.FirstOrDefault(h => h.IsInstallPresent(path) && HasNativeBinaryForThisOs(h.Engine, path));
            if (handler is null)
                return null;

            var engine = handler.Engine;
            var info = EngineFolderInfo.Parse(engine, folderName);
            var recipe = info.RecipeSlug is { } slug
                ? recipes.FirstOrDefault(r => r.Engine == engine && r.BackendType == info.Backend && EngineBuildService.RecipeSlug(r.Name) == slug)
                : null;
            var (os, arch) = ReleaseAssetResolver.DetectHost();
            build = new EngineBuild
            {
                Id = Guid.NewGuid(),
                Engine = engine,
                BackendType = info.Backend,
                Source = info.Source,
                RecipeId = recipe?.Id,
                InstallPath = path,
                VersionTag = info.VersionTag ?? (engine == ServerEngine.TensorFold ? TensorFoldLayout.ReadVersion(path) : null),
                TargetOs = os,
                TargetArch = arch,
            };
            handler.RefreshFromDisk(build);
            build.Name = DefaultName(build, recipe, folderName);
        }

        build.AgentId = null;
        build.Status = handler.IsReady(path) ? EngineBuildStatus.Ready : EngineBuildStatus.NeedsSetup;
        build.SizeBytes = EngineBuildService.TryGetDirectorySize(path);
        build.CreatedAt = Directory.GetCreationTimeUtc(path);
        return build;
    }

    /// <summary>
    /// llama.cpp and stable-diffusion.cpp accept either binary name when checking an install, but a Linux
    /// build in a share a Windows instance also sees can't run there (and the other way round).
    /// </summary>
    private static bool HasNativeBinaryForThisOs(ServerEngine engine, string path)
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        return engine switch
        {
            ServerEngine.LlamaCpp => File.Exists(Path.Combine(path, "llama-server" + exe)),
            ServerEngine.StableDiffusionCpp => File.Exists(Path.Combine(path, "sd-server" + exe)),
            _ => true,
        };
    }

    private static string DefaultName(EngineBuild build, LlamaCppBuildRecipe? recipe, string folderName)
    {
        var engine = EngineReleaseProfile.For(build.Engine)?.DisplayName ?? build.Engine switch
        {
            ServerEngine.Strata => "Strata",
            ServerEngine.Vllm => "vLLM",
            _ => build.Engine.ToString(),
        };
        if (recipe is not null) return $"{recipe.Name} ({folderName})";
        if (build.VersionTag is null) return $"{engine} ({folderName})";
        return build.BackendType == BackendType.Unknown
            ? $"{engine} {build.VersionTag}"
            : $"{engine} {build.VersionTag} ({build.BackendType})";
    }

    // ==================== MODELS ====================

    /// <summary>
    /// Registers model files and checkpoint folders under the library root that aren't in the library yet,
    /// and flags library entries whose files went away (or came back). Returns (added, missing, restored).
    /// </summary>
    public async Task<(int Added, int Missing, int Restored)> DiscoverModelsAsync(CancellationToken ct = default)
    {
        int added = 0, missing = 0, restored = 0;

        var root = (await _modelSettings.GetAsync(ct)).RootFolder;
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            var excluded = await ExcludedAsync(DiscoveryExclusionKind.Model, ct);
            IReadOnlyList<string> candidates;
            try { candidates = await _library.ScanFolderAsync(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not scan the model library root {Root}.", root);
                candidates = Array.Empty<string>();
            }

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var path = NormalizePath(candidate);
                if (excluded.Contains(path) || !IsComplete(path))
                    continue;
                try
                {
                    var model = await _library.ImportFromPathAsync(path);
                    added++;
                    _logger.LogInformation("Registered model {Path} found in the shared model root as '{Name}'.", path, model.Name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not register model {Path}.", path);
                }
            }
        }

        // Another instance may have deleted (or put back) a shared file.
        var local = await _context.LocalModels
            .Where(m => m.AgentId == null && (m.Status == ModelStatus.Ready || m.Status == ModelStatus.Missing))
            .ToListAsync(ct);
        foreach (var model in local)
        {
            var exists = model.Format == ModelFormat.Checkpoint
                ? CheckpointMetadataReader.IsCheckpointFolder(model.FilePath)
                : SplitGguf.AllShards(model.FilePath).All(File.Exists);
            if (exists == (model.Status == ModelStatus.Ready))
                continue;
            try
            {
                await _library.RefreshMetadataAsync(model.Id);
                if (exists) restored++; else missing++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not refresh model {Path}.", model.FilePath);
            }
        }

        return (added, missing, restored);
    }

    /// <summary>
    /// False for files another instance is still downloading: a split model missing shards, or a
    /// checkpoint folder with a <c>.part</c> file in it.
    /// </summary>
    private static bool IsComplete(string path)
    {
        if (Directory.Exists(path))
        {
            try { return !Directory.EnumerateFiles(path, "*.part", SearchOption.AllDirectories).Any(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        return SplitGguf.AllShards(path).All(File.Exists);
    }

    /// <summary>
    /// Links this machine's library entries to the Hugging Face repo their files came from, and keeps the
    /// recorded revision current. Sources, in order: the folder's <see cref="HfSourceManifest"/>, the hub
    /// cache layout, and finally the router's own download layout checked against the Hub. Returns how many
    /// entries were linked or had their revision changed.
    /// </summary>
    public async Task<int> LinkHuggingFaceAsync(bool retryHubLookups = false, CancellationToken ct = default)
    {
        var root = (await _modelSettings.GetAsync(ct)).RootFolder;
        var hasRoot = !string.IsNullOrWhiteSpace(root);
        var models = await _context.LocalModels
            .Where(m => m.AgentId == null && m.Status == ModelStatus.Ready)
            .ToListAsync(ct);

        var linked = 0;
        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var match = HfSourceManifest.Find(model, hasRoot ? root : null);
                if (match is null && HfPathInference.FromHubCache(model.FilePath) is { } cached
                    && (cached.Filename is null) == (model.Format == ModelFormat.Checkpoint))
                    match = cached;

                if (match is not null)
                {
                    if (Apply(model, match)) linked++;
                    continue;
                }

                if (model.Source == ModelSource.HuggingFace && !string.IsNullOrEmpty(model.HfRepoId))
                {
                    // Downloaded here before manifests existed: write one so other instances can link it too.
                    if (model.Format == ModelFormat.Checkpoint)
                        HfSourceManifest.RecordCheckpoint(model.FilePath, model.HfRepoId, model.HfRevision);
                    else if (!string.IsNullOrEmpty(model.HfFilename))
                        HfSourceManifest.RecordFile(model.FilePath, model.HfRepoId, model.HfFilename, model.HfRevision);
                    continue;
                }

                if (hasRoot && await LookUpOnHubAsync(model, root, retryHubLookups, ct) is { } found)
                {
                    if (Apply(model, found)) linked++;
                    if (model.Format == ModelFormat.Checkpoint)
                        HfSourceManifest.RecordCheckpoint(model.FilePath, found.RepoId, found.Revision);
                    else
                        HfSourceManifest.RecordFile(model.FilePath, found.RepoId, found.Filename!, found.Revision);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not work out the Hugging Face source of {Path}.", model.FilePath);
            }
        }

        if (linked > 0)
            await _context.SaveChangesAsync(ct);
        return linked;
    }

    /// <summary>Sets the entry's Hugging Face source. True if anything changed.</summary>
    private static bool Apply(LocalModel model, HfSourceMatch match)
    {
        var sameRepo = string.Equals(model.HfRepoId, match.RepoId, StringComparison.OrdinalIgnoreCase);
        var revision = match.Revision ?? (sameRepo ? model.HfRevision : null);
        if (model.Source == ModelSource.HuggingFace && sameRepo &&
            string.Equals(model.HfFilename, match.Filename, StringComparison.OrdinalIgnoreCase) &&
            model.HfRevision == revision)
            return false;

        model.Source = ModelSource.HuggingFace;
        model.HfRepoId = match.RepoId;
        model.HfFilename = match.Filename;
        model.HfRevision = revision;
        model.UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>
    /// The repo a file under the library root came from, by the router's <c>{owner}_{name}</c> folder
    /// naming, confirmed by the repo listing that file (or, for a checkpoint, being a checkpoint). Its
    /// revision is the repo's current one only when the local file evidently is that version: the same
    /// size, or for a checkpoint every file's blob in the download manifest. Otherwise it's left unknown,
    /// which the update check reports as an update.
    /// </summary>
    private async Task<HfSourceMatch?> LookUpOnHubAsync(LocalModel model, string root, bool retry, CancellationToken ct)
    {
        var checkpoint = model.Format == ModelFormat.Checkpoint;
        var candidates = HfPathInference.FromRouterLayout(root, model.FilePath, checkpoint);
        if (candidates.Count == 0)
            return null;
        if (!retry && FailedHubLookups.TryGetValue(model.FilePath, out var when) && DateTime.UtcNow - when < HubLookupRetry)
            return null;

        foreach (var candidate in candidates)
        {
            var detail = await _hf.GetRepoDetailAsync(candidate.RepoId, ct);
            if (detail is null) continue;

            if (checkpoint)
            {
                var files = CheckpointDownload.Select(detail.Siblings);
                if (files is null) continue;
                var manifest = CheckpointDownload.ReadManifest(model.FilePath);
                var current = manifest.Count > 0 && files.All(f => CheckpointDownload.IsCurrent(model.FilePath, f, manifest));
                FailedHubLookups.TryRemove(model.FilePath, out _);
                return candidate with { RepoId = detail.Id is { Length: > 0 } id ? id : candidate.RepoId, Revision = current ? detail.Sha : null };
            }

            var sibling = detail.Siblings.FirstOrDefault(f => string.Equals(f.Filename, candidate.Filename, StringComparison.OrdinalIgnoreCase));
            if (sibling is null) continue;
            var localSize = new FileInfo(model.FilePath).Length;
            var sameSize = sibling.SizeBytes is { } size && size == localSize;
            FailedHubLookups.TryRemove(model.FilePath, out _);
            return candidate with
            {
                RepoId = detail.Id is { Length: > 0 } repoId ? repoId : candidate.RepoId,
                Filename = sibling.Filename,
                Revision = sameSize ? detail.Sha : null,
            };
        }

        FailedHubLookups[model.FilePath] = DateTime.UtcNow;
        return null;
    }
}
