using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Tracks in-flight Hugging Face model downloads and runs them on background tasks. Singleton so
/// downloads survive across requests; uses <see cref="IServiceScopeFactory"/> per-operation to
/// reach the (scoped) DbContext, following the same pattern as
/// <see cref="LR.Providers.WrapperProcessManager"/>/LlamaCppProvider.
/// </summary>
public class ModelDownloadService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHuggingFaceClient _hfClient;
    private readonly IModelDownloadProgressPublisher _progressPublisher;
    private readonly IModelLibrarySettingsService _settings;
    private readonly ILogger<ModelDownloadService> _logger;

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeDownloads = new();

    public ModelDownloadService(
        IServiceScopeFactory scopeFactory,
        IHuggingFaceClient hfClient,
        IModelDownloadProgressPublisher progressPublisher,
        IModelLibrarySettingsService settings,
        ILogger<ModelDownloadService> logger)
    {
        _scopeFactory = scopeFactory;
        _hfClient = hfClient;
        _progressPublisher = progressPublisher;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Creates a placeholder <see cref="LocalModel"/> row (Status = Downloading) and kicks off the
    /// download on a background task. Returns the model's ID immediately.
    /// </summary>
    public async Task<Guid> StartDownloadAsync(string repoId, string filename, string revision, string? name)
    {
        var root = (await _settings.GetAsync()).RootFolder;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Set a model library root folder before downloading models.");

        var destinationPath = Path.GetFullPath(Path.Combine(root, repoId.Replace('/', '_'), filename));

        Guid modelId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            var model = new LocalModel
            {
                Id = Guid.NewGuid(),
                Name = name ?? DefaultName(repoId, filename),
                FilePath = destinationPath,
                Format = ModelLibraryManager.FormatOfFile(destinationPath),
                Source = ModelSource.HuggingFace,
                Status = ModelStatus.Downloading,
                HfRepoId = repoId,
                HfFilename = filename,
                HfRevision = revision,
            };
            context.LocalModels.Add(model);
            await context.SaveChangesAsync();
            modelId = model.Id;
        }

        var cts = new CancellationTokenSource();
        _activeDownloads[modelId] = cts;

        _ = Task.Run(() => RunDownloadAsync(modelId, repoId, filename, revision, destinationPath, cts.Token));

        return modelId;
    }

    /// <summary>
    /// Downloads a whole Hugging Face checkpoint repo (config, tokenizer, safetensors shards) into one
    /// folder under the library root, as one <see cref="ModelFormat.Checkpoint"/> entry. Checks up front
    /// that the repo is a checkpoint. Returns the model's ID immediately.
    /// </summary>
    public async Task<Guid> StartCheckpointDownloadAsync(string repoId, string? name, CancellationToken ct = default)
    {
        var root = (await _settings.GetAsync()).RootFolder;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Set a model library root folder before downloading models.");

        var detail = await _hfClient.GetRepoDetailAsync(repoId, ct)
            ?? throw new InvalidOperationException($"{repoId} wasn't found on Hugging Face (or is gated: add a token in the library settings).");
        if (CheckpointDownload.Select(detail.Siblings) is null)
            throw new InvalidOperationException($"{repoId} isn't a checkpoint: it needs a config.json and .safetensors weights at its top level.");

        var folder = CheckpointDownload.FolderFor(root, repoId);
        Guid modelId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            if (await context.LocalModels.AnyAsync(m => m.AgentId == null && m.FilePath == folder, ct))
                throw new InvalidOperationException($"{repoId} is already in the library ({folder}). Use Check Update on it instead.");

            var model = new LocalModel
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(name) ? repoId[(repoId.IndexOf('/') + 1)..] : name.Trim(),
                FilePath = folder,
                Format = ModelFormat.Checkpoint,
                Source = ModelSource.HuggingFace,
                Status = ModelStatus.Downloading,
                HfRepoId = repoId,
                HfRevision = detail.Sha,
            };
            context.LocalModels.Add(model);
            await context.SaveChangesAsync(ct);
            modelId = model.Id;
        }

        var cts = new CancellationTokenSource();
        _activeDownloads[modelId] = cts;
        _ = Task.Run(() => RunCheckpointDownloadAsync(modelId, repoId, folder, cts.Token));
        return modelId;
    }

    /// <summary>
    /// Re-downloads a Hugging Face model's file to its existing <see cref="LocalModel.FilePath"/>,
    /// fetching the repo's current default-branch revision. Updates the same <see cref="LocalModel"/>
    /// row in place (same Id and path) rather than creating a new one, so presets that reference it
    /// keep working without re-pointing.
    /// </summary>
    public async Task<Guid> StartUpdateAsync(Guid modelId)
    {
        string repoId, filename, destinationPath;
        bool checkpoint;
        using (var scope = _scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            var model = await context.LocalModels.FindAsync(modelId)
                ?? throw new InvalidOperationException("Model not found.");
            checkpoint = model.Format == ModelFormat.Checkpoint;
            if (model.Source != ModelSource.HuggingFace || string.IsNullOrEmpty(model.HfRepoId) || (!checkpoint && string.IsNullOrEmpty(model.HfFilename)))
                throw new InvalidOperationException("Not a Hugging Face model.");
            if (model.Status == ModelStatus.Downloading)
                throw new InvalidOperationException("Already downloading.");

            repoId = model.HfRepoId;
            filename = model.HfFilename ?? "";
            destinationPath = model.FilePath;

            model.Status = ModelStatus.Downloading;
            model.StatusMessage = null;
            await context.SaveChangesAsync();
        }

        var cts = new CancellationTokenSource();
        _activeDownloads[modelId] = cts;

        _ = checkpoint
            ? Task.Run(() => RunCheckpointDownloadAsync(modelId, repoId, destinationPath, cts.Token))
            : Task.Run(() => RunDownloadAsync(modelId, repoId, filename, "main", destinationPath, cts.Token));

        return modelId;
    }

    /// <summary>
    /// Fetches every checkpoint file of the repo's current revision into <paramref name="folder"/>, skipping
    /// files the folder's manifest says are already there unchanged, so a cancelled download resumes and an
    /// update only fetches what changed. Every file is pinned to the revision the repo listing reported.
    /// </summary>
    private async Task RunCheckpointDownloadAsync(Guid modelId, string repoId, string folder, CancellationToken ct)
    {
        try
        {
            var detail = await _hfClient.GetRepoDetailAsync(repoId, ct)
                ?? throw new InvalidOperationException($"{repoId} wasn't found on Hugging Face.");
            var files = CheckpointDownload.Select(detail.Siblings)
                ?? throw new InvalidOperationException($"{repoId} no longer looks like a checkpoint (no config.json and .safetensors weights).");
            var revision = detail.Sha ?? "main";

            Directory.CreateDirectory(folder);
            var manifest = CheckpointDownload.ReadManifest(folder);
            var pending = files.Where(f => !CheckpointDownload.IsCurrent(folder, f, manifest)).ToList();
            long? total = files.All(f => f.SizeBytes is not null) ? files.Sum(f => f.SizeBytes!.Value) : null;
            long done = files.Except(pending).Sum(f => f.SizeBytes ?? 0);

            foreach (var file in pending)
            {
                long offset = done;
                var progress = new Progress<DownloadProgress>(p =>
                {
                    p.ModelId = modelId;
                    p.BytesReceived += offset;
                    p.TotalBytes = total ?? p.TotalBytes;
                    _ = _progressPublisher.PublishAsync(p);
                });
                var target = Path.GetFullPath(Path.Combine(folder, file.Filename));
                if (!target.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{repoId} lists a file outside its folder ({file.Filename}).");

                await _hfClient.DownloadFileAsync(repoId, file.Filename, revision, target, progress, ct);
                if (file.BlobId is not null)
                {
                    manifest[file.Filename] = file.BlobId;
                    CheckpointDownload.WriteManifest(folder, manifest);
                }
                done += file.SizeBytes ?? new FileInfo(target).Length;
            }

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            var library = scope.ServiceProvider.GetRequiredService<IModelLibrary>();
            if (await context.LocalModels.FindAsync(modelId) is { } model)
            {
                model.HfRevision = detail.Sha ?? model.HfRevision;
                await context.SaveChangesAsync();
                // Lets other router instances sharing the model root link the folder to its repo.
                HfSourceManifest.RecordCheckpoint(folder, repoId, model.HfRevision);
            }

            // Reads config.json, sizes the folder and flips Status -> Ready.
            await library.RefreshMetadataAsync(modelId);
            await _progressPublisher.PublishAsync(new DownloadProgress { ModelId = modelId, Status = "completed" });
        }
        catch (OperationCanceledException)
        {
            await MarkErrorAsync(modelId, "Download cancelled. Downloading again resumes where it stopped.");
            await _progressPublisher.PublishAsync(new DownloadProgress { ModelId = modelId, Status = "error", ErrorMessage = "Cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download checkpoint {RepoId}.", repoId);
            await MarkErrorAsync(modelId, ex.Message);
            await _progressPublisher.PublishAsync(new DownloadProgress { ModelId = modelId, Status = "error", ErrorMessage = ex.Message });
        }
        finally
        {
            _activeDownloads.TryRemove(modelId, out _);
        }
    }

    /// <summary>
    /// Library name for a downloaded file. Quantized GGUFs are usually named after the model
    /// (Qwen3-8B-Q4_K_M.gguf) and keep their file name; a generic name made only of words from the
    /// repo name (turbo.safetensors in krea/Krea-2-Turbo, model.safetensors) uses the repo name
    /// instead, plus the subfolder for diffusers-layout components that all share one file name
    /// (vae/ and unet/diffusion_pytorch_model.safetensors).
    /// </summary>
    public static string DefaultName(string repoId, string filename)
    {
        var stem = Path.ChangeExtension(filename, null);
        var slash = stem.LastIndexOf('/');
        var folder = slash < 0 ? null : stem[..slash];
        var repoName = repoId[(repoId.LastIndexOf('/') + 1)..];

        var repoWords = Words(repoName).Concat(GenericFileWords).ToHashSet();
        if (!Words(stem[(slash + 1)..]).All(repoWords.Contains))
            return stem;

        return folder is null ? repoName : $"{repoName}/{folder}";

        static IEnumerable<string> Words(string s) =>
            Regex.Split(s.ToLowerInvariant(), "[^a-z0-9]+").Where(w => w.Length > 0);
    }

    private static readonly string[] GenericFileWords = ["model", "diffusion", "pytorch"];

    public bool CancelDownload(Guid modelId)
    {
        if (!_activeDownloads.TryGetValue(modelId, out var cts))
            return false;

        cts.Cancel();
        return true;
    }

    private async Task RunDownloadAsync(Guid modelId, string repoId, string filename, string revision, string destinationPath, CancellationToken ct)
    {
        var progress = new Progress<DownloadProgress>(p =>
        {
            p.ModelId = modelId;
            _ = _progressPublisher.PublishAsync(p);
        });

        try
        {
            // A split model (…-00001-of-0000N.gguf) is one library entry: every shard is downloaded next
            // to the first, pinned to the revision the first one resolved to, with progress across all of them.
            var shardNames = SplitGguf.AllShards(filename);
            var shardPaths = SplitGguf.AllShards(destinationPath);
            string? resolvedSha = null;

            if (shardNames.Count == 1)
            {
                resolvedSha = await _hfClient.DownloadFileAsync(repoId, filename, revision, destinationPath, progress, ct);
            }
            else
            {
                var sizes = (await _hfClient.ListModelFilesAsync(repoId, HfModelFormat.Gguf, ct))
                    .ToDictionary(f => f.Filename, f => f.SizeBytes, StringComparer.OrdinalIgnoreCase);
                long? total = shardNames.All(n => sizes.GetValueOrDefault(n) is not null)
                    ? shardNames.Sum(n => sizes[n]!.Value)
                    : null;

                long done = 0;
                for (int i = 0; i < shardNames.Count; i++)
                {
                    long offset = done;
                    var shardProgress = new Progress<DownloadProgress>(p =>
                    {
                        p.ModelId = modelId;
                        p.BytesReceived += offset;
                        p.TotalBytes = total ?? p.TotalBytes;
                        _ = _progressPublisher.PublishAsync(p);
                    });
                    var sha = await _hfClient.DownloadFileAsync(repoId, shardNames[i], resolvedSha ?? revision, shardPaths[i], shardProgress, ct);
                    resolvedSha ??= sha;
                    done += new FileInfo(shardPaths[i]).Length;
                }
            }

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            var library = scope.ServiceProvider.GetRequiredService<IModelLibrary>();

            var model = await context.LocalModels.FindAsync(modelId);
            if (model is not null)
            {
                model.FileSizeBytes = shardPaths.All(File.Exists) ? shardPaths.Sum(p => new FileInfo(p).Length) : null;
                if (!string.IsNullOrEmpty(resolvedSha))
                    model.HfRevision = resolvedSha;
                await context.SaveChangesAsync();
                // Lets other router instances sharing the model root link the file to its repo.
                HfSourceManifest.RecordFile(destinationPath, repoId, filename, model.HfRevision);
            }

            // Re-reads GGUF metadata from the now-downloaded file and flips Status -> Ready.
            await library.RefreshMetadataAsync(modelId);

            await _progressPublisher.PublishAsync(new DownloadProgress { ModelId = modelId, Status = "completed" });
        }
        catch (OperationCanceledException)
        {
            await MarkErrorAsync(modelId, "Download cancelled.");
            await _progressPublisher.PublishAsync(new DownloadProgress { ModelId = modelId, Status = "error", ErrorMessage = "Cancelled" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download {RepoId}/{Filename}.", repoId, filename);
            await MarkErrorAsync(modelId, ex.Message);
            await _progressPublisher.PublishAsync(new DownloadProgress { ModelId = modelId, Status = "error", ErrorMessage = ex.Message });
        }
        finally
        {
            _activeDownloads.TryRemove(modelId, out _);
        }
    }

    private async Task MarkErrorAsync(Guid modelId, string message)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var model = await context.LocalModels.FindAsync(modelId);
        if (model is null) return;

        model.Status = ModelStatus.Error;
        model.StatusMessage = message;
        await context.SaveChangesAsync();
    }
}
