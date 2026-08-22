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
        var list = await _context.LocalModels.OrderBy(m => m.Name).ToListAsync();
        return list.AsReadOnly();
    }

    public async Task<LocalModel?> GetByIdAsync(Guid id)
    {
        return await _context.LocalModels.FindAsync(id);
    }

    public async Task<LocalModel> ImportFromPathAsync(string filePath, string? name = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Model file not found: {filePath}", filePath);

        var normalizedPath = Path.GetFullPath(filePath);
        var existing = await _context.LocalModels.FirstOrDefaultAsync(m => m.FilePath == normalizedPath);
        if (existing is not null)
            throw new InvalidOperationException($"A model already exists for path: {normalizedPath}");

        var model = new LocalModel
        {
            Id = Guid.NewGuid(),
            Name = name ?? Path.GetFileNameWithoutExtension(filePath),
            FilePath = normalizedPath,
            FileSizeBytes = new FileInfo(normalizedPath).Length,
            Source = ModelSource.Local,
            Status = ModelStatus.Ready,
        };

        await ApplyGgufMetadataAsync(model, normalizedPath);

        _context.LocalModels.Add(model);
        await _context.SaveChangesAsync();
        return model;
    }

    public async Task<IReadOnlyList<string>> ScanFolderAsync(string folder)
    {
        if (!Directory.Exists(folder))
            return Array.Empty<string>();

        var registered = await _context.LocalModels.Select(m => m.FilePath).ToListAsync();
        var registeredSet = new HashSet<string>(registered, StringComparer.OrdinalIgnoreCase);

        return Directory.EnumerateFiles(folder, "*.gguf", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(p => !registeredSet.Contains(p))
            .OrderBy(p => p)
            .ToList();
    }

    public async Task<bool> RefreshMetadataAsync(Guid id)
    {
        var model = await _context.LocalModels.FindAsync(id);
        if (model is null) return false;

        if (!File.Exists(model.FilePath))
        {
            model.Status = ModelStatus.Missing;
            model.StatusMessage = "File not found on disk.";
            await _context.SaveChangesAsync();
            return true;
        }

        model.FileSizeBytes = new FileInfo(model.FilePath).Length;
        await ApplyGgufMetadataAsync(model, model.FilePath);
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

        if (deleteFile && File.Exists(model.FilePath))
        {
            try { File.Delete(model.FilePath); }
            catch { /* best effort — registry entry is already gone */ }
        }

        return true;
    }

    public async Task<int> GetPresetUsageCountAsync(Guid id)
    {
        return await _context.ModelPresets.CountAsync(p => p.ModelId == id);
    }

    private async Task ApplyGgufMetadataAsync(LocalModel model, string filePath)
    {
        var metadata = await _ggufReader.ReadAsync(filePath);
        if (metadata is null)
            return;

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
        model.LicenseText = metadata.LicenseText;
        model.AllKvPairsJson = metadata.AllKvPairs is not null
            ? JsonSerializer.Serialize(metadata.AllKvPairs)
            : null;
        model.DetectedMmprojPath = MmprojLocator.FindSiblingMmproj(filePath);

        if (string.IsNullOrWhiteSpace(model.Name) && !string.IsNullOrWhiteSpace(metadata.ModelName))
            model.Name = metadata.ModelName!;
    }
}
