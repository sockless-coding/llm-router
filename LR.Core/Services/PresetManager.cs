using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Preset manager with SQLite persistence via EF Core.
/// </summary>
public class PresetManager : IPresetManager
{
    private readonly LRDbContext _context;
    private readonly IGgufMetadataReader? _ggufReader;

    public PresetManager(LRDbContext context, IGgufMetadataReader? ggufReader = null)
    {
        _context = context;
        _ggufReader = ggufReader;
    }

    /// <summary>
    /// Gets all presets across all server instances.
    /// </summary>
    public IReadOnlyList<ModelPreset> GetAllPresets()
    {
        return _context.ModelPresets.ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets all presets across all server instances (async).
    /// </summary>
    public async Task<IReadOnlyList<ModelPreset>> GetAllPresetsAsync()
    {
        var list = await _context.ModelPresets.ToListAsync();
        return new List<ModelPreset>(list).AsReadOnly();
    }

    public async Task<ModelPreset> CreateAsync(ModelPreset preset)
    {
        if (preset.Id == Guid.Empty)
            throw new ArgumentException("Preset must have a valid ID.", nameof(preset));

        // A registry model link takes precedence over whatever ModelPath was passed in —
        // resolve it to the model's file path and copy its already-read GGUF metadata instead
        // of re-parsing the file.
        if (preset.ModelId.HasValue)
            await ApplyLinkedModelAsync(preset, preset.ModelId.Value);

        // Same for the draft model / mmproj registry links — resolved after the main model link
        // so an explicit mmproj pick always wins over the main model's auto-detected sibling.
        await ApplyLinkedDraftModelAsync(preset);
        await ApplyLinkedMmprojAsync(preset);

        _context.ModelPresets.Add(preset);
        await _context.SaveChangesAsync();

        // No registry link — read GGUF metadata directly from the manually-entered path.
        if (!preset.ModelId.HasValue && _ggufReader != null)
            await ReadGgufMetadataAsync(preset, preset.ModelPath);

        return preset;
    }

    /// <summary>
    /// Resolves <paramref name="modelId"/> against the model registry and copies its file path +
    /// GGUF metadata onto the preset. No-ops (leaving ModelPath/GGUF fields untouched) if the
    /// model can't be found, so a stale link never blanks out a working preset.
    /// </summary>
    private async Task ApplyLinkedModelAsync(ModelPreset preset, Guid modelId)
    {
        var model = await _context.LocalModels.FindAsync(modelId);
        if (model is null)
            return;

        preset.ModelPath = model.FilePath;
        PresetGgufSync.ApplyFromModel(preset, model);
    }

    /// <summary>
    /// Resolves <see cref="ModelPreset.SpecDraftModelId"/> against the model registry and copies
    /// its file path onto <see cref="ModelPreset.SpecDraftModel"/>. No-ops if unset or the model
    /// can't be found, leaving a manually-entered path untouched.
    /// </summary>
    private async Task ApplyLinkedDraftModelAsync(ModelPreset preset)
    {
        if (!preset.SpecDraftModelId.HasValue)
            return;

        var model = await _context.LocalModels.FindAsync(preset.SpecDraftModelId.Value);
        if (model is null)
            return;

        preset.SpecDraftModel = model.FilePath;
    }

    /// <summary>
    /// Resolves <see cref="ModelPreset.MmprojId"/> against the model registry and copies its file
    /// path onto <see cref="ModelPreset.Mmproj"/>. No-ops if unset or the model can't be found,
    /// leaving a manually-entered path (or an auto-detected sibling projector) untouched.
    /// </summary>
    private async Task ApplyLinkedMmprojAsync(ModelPreset preset)
    {
        if (!preset.MmprojId.HasValue)
            return;

        var model = await _context.LocalModels.FindAsync(preset.MmprojId.Value);
        if (model is null)
            return;

        preset.Mmproj = model.FilePath;
    }

    private async Task ReadGgufMetadataAsync(ModelPreset preset, string? modelPath)
    {
        if (string.IsNullOrEmpty(modelPath) || _ggufReader == null)
            return;

        var metadata = await _ggufReader.ReadAsync(modelPath);
        if (metadata is null)
            return;

        preset.GgufArchitecture = metadata.Architecture;
        preset.GgufModelName = metadata.ModelName;
        preset.GgufParameterSize = metadata.ParameterSize;
        preset.GgufQuantizationLevel = metadata.QuantizationLevel;
        preset.GgufContextLength = metadata.ContextLength;
        preset.GgufEmbeddingLength = metadata.EmbeddingLength;
        preset.GgufRopeFreqBase = metadata.RopeFreqBase;
        preset.GgufChatTemplate = metadata.ChatTemplate;

        if (string.IsNullOrEmpty(preset.Mmproj) && string.IsNullOrEmpty(preset.MmprojUrl))
        {
            var detectedMmproj = MmprojLocator.FindSiblingMmproj(modelPath);
            if (!string.IsNullOrEmpty(detectedMmproj))
                preset.Mmproj = detectedMmproj;
        }

        await _context.SaveChangesAsync();
    }

    public async Task<bool> UpdateAsync(Guid presetId, ModelPreset updated)
    {
        var existing = await _context.ModelPresets.FindAsync(presetId);
        if (existing is null) return false;

        // Track whether model path changed so we can re-read GGUF metadata
        var oldModelPath = existing.ModelPath;
        var pathChanged = !string.Equals(oldModelPath, updated.ModelPath, StringComparison.Ordinal);

        // Copies every scalar property (by name) from `updated` onto the tracked `existing`
        // entity in one shot — this is what keeps the ~130-field list in sync automatically as
        // properties are added, instead of needing a matching assignment line here for each one
        // (a previous hand-written version of this method silently missed about a quarter of the
        // fields added after it was first written). Navigation properties aren't touched.
        _context.Entry(existing).CurrentValues.SetValues(updated);

        // A registry link takes precedence: resolve it to the model's file path + metadata,
        // overwriting whatever ModelPath/Gguf* values were just assigned above.
        if (existing.ModelId.HasValue)
            await ApplyLinkedModelAsync(existing, existing.ModelId.Value);

        await ApplyLinkedDraftModelAsync(existing);
        await ApplyLinkedMmprojAsync(existing);

        await _context.SaveChangesAsync();

        // No registry link — re-read GGUF metadata directly if the manually-entered path changed.
        if (!existing.ModelId.HasValue && pathChanged && _ggufReader != null)
            await ReadGgufMetadataAsync(existing, existing.ModelPath);

        return true;
    }

    public async Task<bool> DeleteAsync(Guid presetId)
    {
        var existing = await _context.ModelPresets.FindAsync(presetId);
        if (existing is null) return false;

        _context.ModelPresets.Remove(existing);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<IReadOnlyList<ModelPreset>> GetByServerInstanceIdAsync(Guid serverInstanceId)
    {
        var presets = await _context.ModelPresets
            .Where(p => p.ServerInstanceId == serverInstanceId)
            .ToListAsync();
        return presets.AsReadOnly();
    }

    public IReadOnlyList<ModelPreset> GetByServerInstanceId(Guid serverInstanceId)
    {
        return _context.ModelPresets
            .Where(p => p.ServerInstanceId == serverInstanceId)
            .ToList().AsReadOnly();
    }

    public async Task<ModelPreset?> GetByIdAsync(Guid presetId)
    {
        return await _context.ModelPresets.FindAsync(presetId);
    }

    public ModelPreset? GetById(Guid presetId)
    {
        return _context.ModelPresets.Find(presetId);
    }
}
