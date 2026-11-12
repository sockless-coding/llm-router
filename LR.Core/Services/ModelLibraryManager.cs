using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Model registry with SQLite persistence via EF Core. Mirrors <see cref="PresetManager"/>'s shape.
/// </summary>
public class ModelLibraryManager : IModelLibrary
{
    private readonly LRDbContext _context;
    private readonly IGgufMetadataReader _ggufReader;

    public ModelLibraryManager(LRDbContext context, IGgufMetadataReader ggufReader)
    {
        _context = context;
        _ggufReader = ggufReader;
    }

    public async Task<IReadOnlyList<LocalModel>> GetAllAsync()
    {
        var list = await _context.LocalModels.Include(m => m.Agent).OrderBy(m => m.Name).ToListAsync();
        return list.AsReadOnly();
    }

    public async Task<LocalModel?> GetByIdAsync(Guid id)
    {
        return await _context.LocalModels.FindAsync(id);
    }

    public async Task<LocalModel> ImportFromPathAsync(string filePath, string? name = null)
    {
        if (Directory.Exists(filePath))
            return await ImportCheckpointAsync(filePath, name);

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Model file not found: {filePath}", filePath);

        var normalizedPath = Path.GetFullPath(filePath);
        var existing = await _context.LocalModels.FirstOrDefaultAsync(m => m.FilePath == normalizedPath && m.AgentId == null);
        if (existing is not null)
            throw new InvalidOperationException($"A model already exists for path: {normalizedPath}");

        var model = new LocalModel
        {
            Id = Guid.NewGuid(),
            Name = name ?? Path.GetFileNameWithoutExtension(filePath),
            FilePath = normalizedPath,
            FileSizeBytes = new FileInfo(normalizedPath).Length,
            Format = FormatOfFile(normalizedPath),
            Source = ModelSource.Local,
            Status = ModelStatus.Ready,
        };

        await ApplyGgufMetadataAsync(model, normalizedPath);

        _context.LocalModels.Add(model);
        await _context.SaveChangesAsync();
        await SharedLibraryDiscovery.ClearExclusionAsync(_context, DiscoveryExclusionKind.Model, model.FilePath);
        return model;
    }

    public async Task<IReadOnlyList<string>> ScanFolderAsync(string folder)
    {
        if (!Directory.Exists(folder))
            return Array.Empty<string>();

        var registered = await _context.LocalModels.Where(m => m.AgentId == null).Select(m => m.FilePath).ToListAsync();
        var registeredSet = new HashSet<string>(registered.Select(TrimSeparators), StringComparer.OrdinalIgnoreCase);

        // A checkpoint folder is one entry; the safetensors shards inside it aren't models of their own.
        var checkpoints = new[] { folder }.Concat(Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories))
            .Where(CheckpointMetadataReader.IsCheckpointFolder)
            .Select(d => TrimSeparators(Path.GetFullPath(d)))
            .ToList();
        bool InCheckpoint(string file) => checkpoints.Any(c => file.StartsWith(c + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(IsModelFile)
            .Select(Path.GetFullPath)
            .Where(p => !InCheckpoint(p))
            // shards 2..N of a split model belong to its first shard's entry
            .Where(p => SplitGguf.Parse(p) is not { Index: > 1 });

        return checkpoints.Concat(files)
            .Where(p => !registeredSet.Contains(p))
            .OrderBy(p => p)
            .ToList();
    }

    /// <summary>Registers a checkpoint folder (config.json and safetensors weights), read from its config.</summary>
    private async Task<LocalModel> ImportCheckpointAsync(string folder, string? name)
    {
        var normalized = TrimSeparators(Path.GetFullPath(folder));
        if (!CheckpointMetadataReader.IsCheckpointFolder(normalized))
            throw new InvalidOperationException($"'{normalized}' isn't a checkpoint folder: it needs a config.json and .safetensors weights.");
        if (await _context.LocalModels.AnyAsync(m => m.FilePath == normalized && m.AgentId == null))
            throw new InvalidOperationException($"A model already exists for path: {normalized}");

        var model = new LocalModel
        {
            Id = Guid.NewGuid(),
            Name = name ?? Path.GetFileName(normalized),
            FilePath = normalized,
            Format = ModelFormat.Checkpoint,
            FileSizeBytes = CheckpointMetadataReader.FolderSize(normalized),
            Source = ModelSource.Local,
            Status = ModelStatus.Ready,
        };
        CheckpointMetadataReader.Apply(model);

        _context.LocalModels.Add(model);
        await _context.SaveChangesAsync();
        await SharedLibraryDiscovery.ClearExclusionAsync(_context, DiscoveryExclusionKind.Model, model.FilePath);
        return model;
    }

    private static string TrimSeparators(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>The format of a model file, from its extension.</summary>
    public static ModelFormat FormatOfFile(string path) =>
        path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sft", StringComparison.OrdinalIgnoreCase)
            ? ModelFormat.Safetensors
            : ModelFormat.Gguf;

    public async Task<bool> RefreshMetadataAsync(Guid id)
    {
        var model = await _context.LocalModels.FindAsync(id);
        if (model is null) return false;

        // A model on a remote agent is read by that agent (see AgentDeploymentService), never from this disk.
        if (model.AgentId is not null)
            throw new InvalidOperationException("This model lives on a remote agent — refresh it from the agent.");

        if (model.Format == ModelFormat.Checkpoint)
        {
            if (!CheckpointMetadataReader.IsCheckpointFolder(model.FilePath))
            {
                model.Status = ModelStatus.Missing;
                model.StatusMessage = "Checkpoint folder (config.json and .safetensors weights) not found on disk.";
                await _context.SaveChangesAsync();
                return true;
            }
            model.FileSizeBytes = CheckpointMetadataReader.FolderSize(model.FilePath);
            CheckpointMetadataReader.Apply(model);
        }
        else if (!File.Exists(model.FilePath))
        {
            model.Status = ModelStatus.Missing;
            model.StatusMessage = "File not found on disk.";
            await _context.SaveChangesAsync();
            return true;
        }
        else
        {
            model.FileSizeBytes = new FileInfo(model.FilePath).Length;
            await ApplyGgufMetadataAsync(model, model.FilePath);
        }
        model.Status = ModelStatus.Ready;
        model.StatusMessage = null;
        model.LastVerifiedAt = DateTimeOffset.UtcNow;
        model.UpdatedAt = DateTimeOffset.UtcNow;

        // A model's metadata is only read here, but any preset linking to it caches its own copy
        // (for launch-time use without a join) — keep those in sync, not just the model row.
        var linkedPresets = await _context.ModelPresets.Where(p => p.ModelId == id).ToListAsync();
        foreach (var preset in linkedPresets)
            PresetGgufSync.ApplyFromModel(preset, model);

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> UpdateAsync(Guid id, string name, string? notes)
    {
        var model = await _context.LocalModels.FindAsync(id);
        if (model is null) return false;

        model.Name = name;
        model.Notes = notes;
        model.UpdatedAt = DateTimeOffset.UtcNow;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid id, bool deleteFile)
    {
        var model = await _context.LocalModels.FindAsync(id);
        if (model is null) return false;

        // Presets referencing this model keep their ModelPath but lose the link (SetNull FK).
        _context.LocalModels.Remove(model);
        await _context.SaveChangesAsync();

        // A remote agent's file is deleted by that agent; never touch a same-named path here.
        if (deleteFile && model.AgentId is null && model.Format == ModelFormat.Checkpoint)
        {
            try { if (Directory.Exists(model.FilePath)) Directory.Delete(model.FilePath, recursive: true); }
            catch { /* best effort — registry entry is already gone */ }
        }
        else if (deleteFile && model.AgentId is null)
        {
            // every shard of a split model
            foreach (var path in SplitGguf.AllShards(model.FilePath).Where(File.Exists))
            {
                try { File.Delete(path); }
                catch { /* best effort — registry entry is already gone */ }
            }
        }

        // Files left on disk would otherwise be registered again by discovery on the next scan.
        if (model.AgentId is null && (File.Exists(model.FilePath) || Directory.Exists(model.FilePath)))
            await SharedLibraryDiscovery.ExcludeAsync(_context, DiscoveryExclusionKind.Model, model.FilePath);

        return true;
    }

    public async Task<int> GetPresetUsageCountAsync(Guid id)
    {
        return await _context.ModelPresets.CountAsync(p => p.ModelId == id);
    }

    /// <summary>
    /// GGUF (llama.cpp, Strata, stable-diffusion.cpp) or safetensors — the format most diffusion
    /// checkpoints, VAEs and text encoders ship in, which stable-diffusion.cpp loads as is.
    /// </summary>
    private static bool IsModelFile(string path) =>
        path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase);

    private async Task ApplyGgufMetadataAsync(LocalModel model, string filePath)
    {
        var metadata = await _ggufReader.ReadAsync(filePath);
        if (metadata is null)
            return;

        ApplyGgufMetadata(model, metadata, MmprojLocator.FindSiblingMmproj(filePath));
    }

    /// <summary>
    /// Copies GGUF metadata onto a library entry. Public so metadata read elsewhere (a remote agent
    /// reads the file on its own disk) lands on the row exactly as a local read would.
    /// </summary>
    public static void ApplyGgufMetadata(LocalModel model, GgufMetadata metadata, string? detectedMmprojPath)
    {
        model.Architecture = metadata.Architecture;
        model.GgufModelName = metadata.ModelName;
        model.ParameterSize = metadata.ParameterSize;
        model.QuantizationLevel = metadata.QuantizationLevel;
        model.ContextLength = metadata.ContextLength;
        model.EmbeddingLength = metadata.EmbeddingLength;
        model.FeedForwardLength = metadata.FeedForwardLength;
        model.BlockCount = metadata.BlockCount;
        model.HeadCount = metadata.HeadCount;
        model.KvHeadCount = metadata.KvHeadCount;
        model.RopeFreqBase = metadata.RopeFreqBase;
        model.EosTokenId = metadata.EosTokenId;
        model.BosTokenId = metadata.BosTokenId;
        model.ChatTemplate = metadata.ChatTemplate;
        model.DecisionType = metadata.DecisionType;
        model.PoolingType = metadata.PoolingType;
        model.LicenseText = metadata.LicenseText;
        model.AllKvPairsJson = metadata.AllKvPairs is not null
            ? JsonSerializer.Serialize(metadata.AllKvPairs)
            : null;
        model.DetectedMmprojPath = detectedMmprojPath;

        if (string.IsNullOrWhiteSpace(model.Name) && !string.IsNullOrWhiteSpace(metadata.ModelName))
            model.Name = metadata.ModelName!;
    }
}
