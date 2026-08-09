using System.Collections.Concurrent;

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
                Name = name ?? Path.GetFileNameWithoutExtension(filename),
                FilePath = destinationPath,
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
            var resolvedSha = await _hfClient.DownloadFileAsync(repoId, filename, revision, destinationPath, progress, ct);

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            var library = scope.ServiceProvider.GetRequiredService<IModelLibrary>();

            var model = await context.LocalModels.FindAsync(modelId);
            if (model is not null)
            {
                model.FileSizeBytes = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : null;
                if (!string.IsNullOrEmpty(resolvedSha))
                    model.HfRevision = resolvedSha;
                await context.SaveChangesAsync();
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
