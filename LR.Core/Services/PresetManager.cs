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
        preset.GgufArchitecture = model.Architecture;
        preset.GgufModelName = model.GgufModelName;
        preset.GgufParameterSize = model.ParameterSize;
        preset.GgufQuantizationLevel = model.QuantizationLevel;
        preset.GgufContextLength = model.ContextLength;
        preset.GgufEmbeddingLength = model.EmbeddingLength;
        preset.GgufRopeFreqBase = model.RopeFreqBase;
        preset.GgufChatTemplate = model.ChatTemplate;
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

        await _context.SaveChangesAsync();
    }

    public async Task<bool> UpdateAsync(Guid presetId, ModelPreset updated)
    {
        var existing = await _context.ModelPresets.FindAsync(presetId);
        if (existing is null) return false;

        // Track whether model path changed so we can re-read GGUF metadata
        var oldModelPath = existing.ModelPath;
        var pathChanged = !string.Equals(oldModelPath, updated.ModelPath, StringComparison.Ordinal);

        existing.Name = updated.Name;
        existing.ModelPath = updated.ModelPath;
        existing.ModelId = updated.ModelId;

        // Core settings
        existing.ContextSize = updated.ContextSize;
        existing.GpuLayers = updated.GpuLayers;
        existing.CacheTypeK = updated.CacheTypeK;
        existing.CacheTypeV = updated.CacheTypeV;
        existing.FlashAttention = updated.FlashAttention;
        existing.Jinja = updated.Jinja;
        existing.SpecType = updated.SpecType;

        // Sampling (core)
        existing.Temperature = updated.Temperature;
        existing.TopK = updated.TopK;
        existing.MinP = updated.MinP;
        existing.TopP = updated.TopP;
        existing.RepeatPenalty = updated.RepeatPenalty;
        existing.PresencePenalty = updated.PresencePenalty;

        // Advanced: Generation
        existing.Threads = updated.Threads;
        existing.ThreadsBatch = updated.ThreadsBatch;
        existing.PredictN = updated.PredictN;
        existing.BatchSize = updated.BatchSize;
        existing.UbatchSize = updated.UbatchSize;
        existing.KeepN = updated.KeepN;
        existing.Seed = updated.Seed;
        existing.IgnoreEos = updated.IgnoreEos;

        // Advanced: GPU/Device
        existing.Device = updated.Device;
        existing.SplitMode = updated.SplitMode;
        existing.TensorSplit = updated.TensorSplit;
        existing.MainGpu = updated.MainGpu;
        existing.Fit = updated.Fit;
        existing.KvOffload = updated.KvOffload;
        existing.Repack = updated.Repack;

        // Advanced: Memory
        existing.LoadMode = updated.LoadMode;
        existing.CacheRam = updated.CacheRam;

        // Advanced: RoPE Scaling
        existing.RopeScalingType = updated.RopeScalingType;
        existing.RopeScale = updated.RopeScale;
        existing.RopeFreqBase = updated.RopeFreqBase;
        existing.RopeFreqScale = updated.RopeFreqScale;
        existing.YarnOrigCtx = updated.YarnOrigCtx;
        existing.YarnExtFactor = updated.YarnExtFactor;
        existing.YarnAttnFactor = updated.YarnAttnFactor;
        existing.YarnBetaSlow = updated.YarnBetaSlow;
        existing.YarnBetaFast = updated.YarnBetaFast;

        // Advanced: Sampling (Extended)
        existing.TopNSigma = updated.TopNSigma;
        existing.XtcProbability = updated.XtcProbability;
        existing.XtcThreshold = updated.XtcThreshold;
        existing.TypicalP = updated.TypicalP;
        existing.RepeatLastN = updated.RepeatLastN;
        existing.FrequencyPenalty = updated.FrequencyPenalty;
        existing.DryMultiplier = updated.DryMultiplier;
        existing.DryBase = updated.DryBase;
        existing.DryAllowedLength = updated.DryAllowedLength;
        existing.DryPenaltyLastN = updated.DryPenaltyLastN;
        existing.Mirostat = updated.Mirostat;
        existing.MirostatTau = updated.MirostatTau;
        existing.MirostatEta = updated.MirostatEta;
        existing.DynatempRange = updated.DynatempRange;
        existing.DynatempExp = updated.DynatempExp;

        // Advanced: Speculative Decoding
        existing.SpecDraftModel = updated.SpecDraftModel;
        existing.SpecDraftNMax = updated.SpecDraftNMax;
        existing.SpecDraftNMin = updated.SpecDraftNMin;
        existing.SpecDraftPMin = updated.SpecDraftPMin;
        existing.SpecDraftTypeK = updated.SpecDraftTypeK;
        existing.SpecDraftTypeV = updated.SpecDraftTypeV;
        existing.SpecDraftGpuLayers = updated.SpecDraftGpuLayers;
        existing.SpecDraftThreads = updated.SpecDraftThreads;

        // Advanced: Server
        existing.Host = updated.Host;
        existing.Port = updated.Port;
        existing.Parallel = updated.Parallel;
        existing.ContBatching = updated.ContBatching;
        existing.Timeout = updated.Timeout;
        existing.ApiKey = updated.ApiKey;
        existing.CachePrompt = updated.CachePrompt;

        // Advanced: Reasoning
        existing.Reasoning = updated.Reasoning;
        existing.ReasoningBudget = updated.ReasoningBudget;
        existing.ReasoningFormat = updated.ReasoningFormat;

        // Advanced: Multimodal
        existing.Mmproj = updated.Mmproj;
        existing.ImageMinTokens = updated.ImageMinTokens;
        existing.ImageMaxTokens = updated.ImageMaxTokens;

        // Advanced: LoRA
        existing.Lora = updated.Lora;

        // Advanced: Chat Template
        existing.ChatTemplate = updated.ChatTemplate;

        // GGUF Metadata (auto-read from file)
        existing.GgufArchitecture = updated.GgufArchitecture;
        existing.GgufModelName = updated.GgufModelName;
        existing.GgufParameterSize = updated.GgufParameterSize;
        existing.GgufQuantizationLevel = updated.GgufQuantizationLevel;
        existing.GgufContextLength = updated.GgufContextLength;
        existing.GgufEmbeddingLength = updated.GgufEmbeddingLength;
        existing.GgufRopeFreqBase = updated.GgufRopeFreqBase;
        existing.GgufChatTemplate = updated.GgufChatTemplate;

        // Fallback flags
        existing.Flags = updated.Flags;

        // A registry link takes precedence: resolve it to the model's file path + metadata,
        // overwriting whatever ModelPath/Gguf* values were just assigned above.
        if (existing.ModelId.HasValue)
            await ApplyLinkedModelAsync(existing, existing.ModelId.Value);

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
