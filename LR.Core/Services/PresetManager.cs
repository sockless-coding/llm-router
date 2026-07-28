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

    public PresetManager(LRDbContext context)
    {
        _context = context;
    }

    public async Task<ModelPreset> CreateAsync(ModelPreset preset)
    {
        if (preset.Id == Guid.Empty)
            throw new ArgumentException("Preset must have a valid ID.", nameof(preset));

        _context.ModelPresets.Add(preset);
        await _context.SaveChangesAsync();
        return preset;
    }

    public async Task<bool> UpdateAsync(Guid presetId, ModelPreset updated)
    {
        var existing = await _context.ModelPresets.FindAsync(presetId);
        if (existing is null) return false;

        existing.Name = updated.Name;
        existing.ModelPath = updated.ModelPath;

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

        // Advanced: Multimodal
        existing.Mmproj = updated.Mmproj;
        existing.ImageMinTokens = updated.ImageMinTokens;
        existing.ImageMaxTokens = updated.ImageMaxTokens;

        // Advanced: LoRA
        existing.Lora = updated.Lora;

        // Advanced: Chat Template
        existing.ChatTemplate = updated.ChatTemplate;

        // Fallback flags
        existing.Flags = updated.Flags;

        await _context.SaveChangesAsync();
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
