using System.ComponentModel.DataAnnotations;

namespace LR.Application.Pages.Features.Presets;

public class PresetViewModel
{
    public Guid ServerInstanceId { get; set; }

    [Required, MaxLength(256)]
    public string Name { get; set; } = "";

    /// <summary>
    /// Selected model from the registry. When set, this takes precedence over
    /// <see cref="ModelPath"/> — see <c>PresetManager.ApplyLinkedModelAsync</c>.
    /// </summary>
    public Guid? ModelId { get; set; }

    /// <summary>
    /// Manual path override, used when no registry model is selected. Nullable (not [Required])
    /// because validity depends on ModelId — enforced explicitly in the page handlers. Must stay
    /// nullable: with Nullable enabled project-wide, a non-nullable string here would make the
    /// tag helpers render an implicit "required" attribute on the input, blocking submission
    /// whenever a model is picked from the dropdown instead of typed manually.
    /// </summary>
    [MaxLength(1024)]
    public string? ModelPath { get; set; }

    // Core
    public int? ContextSize { get; set; }
    public int? GpuLayers { get; set; }
    public string? CacheTypeK { get; set; }
    public string? CacheTypeV { get; set; }
    public string? FlashAttention { get; set; }
    public bool? Jinja { get; set; }
    public string? SpecType { get; set; }

    // Sampling (core)
    public float? Temperature { get; set; }
    public int? TopK { get; set; }
    public float? MinP { get; set; }
    public float? TopP { get; set; }
    public float? RepeatPenalty { get; set; }
    public float? PresencePenalty { get; set; }

    // Advanced: Generation
    public int? Threads { get; set; }
    public int? ThreadsBatch { get; set; }
    public int? PredictN { get; set; }
    public int? BatchSize { get; set; }
    public int? UbatchSize { get; set; }
    public int? KeepN { get; set; }
    public long? Seed { get; set; }
    public bool? IgnoreEos { get; set; }

    // Advanced: GPU/Device
    public string? Device { get; set; }
    public string? SplitMode { get; set; }
    public string? TensorSplit { get; set; }
    public int? MainGpu { get; set; }
    public string? Fit { get; set; }
    public string? FitTarget { get; set; }
    public int? FitCtx { get; set; }
    public bool? KvOffload { get; set; }
    public bool? Repack { get; set; }
    public bool? CpuMoe { get; set; }
    public int? NCpuMoe { get; set; }
    public string? OverrideTensor { get; set; }

    // Advanced: Memory
    public string? LoadMode { get; set; }
    public int? CacheRam { get; set; }

    // Advanced: RoPE Scaling
    public string? RopeScalingType { get; set; }
    public float? RopeScale { get; set; }
    public float? RopeFreqBase { get; set; }
    public float? RopeFreqScale { get; set; }
    public int? YarnOrigCtx { get; set; }
    public float? YarnExtFactor { get; set; }
    public float? YarnAttnFactor { get; set; }
    public float? YarnBetaSlow { get; set; }
    public float? YarnBetaFast { get; set; }

    // Advanced: Sampling (Extended)
    public string? Samplers { get; set; }
    public string? SamplerSeq { get; set; }
    public float? TopNSigma { get; set; }
    public float? XtcProbability { get; set; }
    public float? XtcThreshold { get; set; }
    public float? TypicalP { get; set; }
    public int? RepeatLastN { get; set; }
    public float? FrequencyPenalty { get; set; }
    public float? DryMultiplier { get; set; }
    public float? DryBase { get; set; }
    public int? DryAllowedLength { get; set; }
    public int? DryPenaltyLastN { get; set; }
    public string? DrySequenceBreaker { get; set; }
    public int? Mirostat { get; set; }
    public float? MirostatTau { get; set; }
    public float? MirostatEta { get; set; }
    public float? DynatempRange { get; set; }
    public float? DynatempExp { get; set; }

    // Advanced: Speculative Decoding
    public string? SpecDraftModel { get; set; }
    public int? SpecDraftNMax { get; set; }
    public int? SpecDraftNMin { get; set; }
    public float? SpecDraftPMin { get; set; }
    public float? SpecDraftPSplit { get; set; }
    public string? SpecDraftTypeK { get; set; }
    public string? SpecDraftTypeV { get; set; }
    public int? SpecDraftGpuLayers { get; set; }
    public int? SpecDraftThreads { get; set; }

    // Advanced: Server
    public string? Host { get; set; }
    public int? Port { get; set; }
    public int? Parallel { get; set; }
    public bool? ContBatching { get; set; }
    public int? Timeout { get; set; }
    public string? ApiKey { get; set; }
    public bool? CachePrompt { get; set; }
    public int? CacheReuse { get; set; }
    public bool? ContextShift { get; set; }
    public bool? KvUnified { get; set; }
    public float? SlotPromptSimilarity { get; set; }
    public int? SleepIdleSeconds { get; set; }

    // Advanced: Reasoning
    public string? Reasoning { get; set; }
    public string? ReasoningEffort { get; set; }
    public int? ReasoningBudget { get; set; }
    public string? ReasoningBudgetMessage { get; set; }
    public string? ReasoningFormat { get; set; }
    public bool? ReasoningPreserve { get; set; }

    // Advanced: Multimodal
    public string? Mmproj { get; set; }
    public string? MmprojUrl { get; set; }
    public bool? MmprojAuto { get; set; }
    public bool? MmprojOffload { get; set; }
    public string? MmprojDevice { get; set; }
    public int? ImageMinTokens { get; set; }
    public int? ImageMaxTokens { get; set; }
    public int? MtmdBatchMaxTokens { get; set; }

    // Advanced: LoRA
    public string? Lora { get; set; }
    public string? LoraScaled { get; set; }
    public string? ControlVector { get; set; }
    public string? ControlVectorScaled { get; set; }
    public int? ControlVectorLayerStart { get; set; }
    public int? ControlVectorLayerEnd { get; set; }

    // Advanced: Chat Template
    public string? ChatTemplate { get; set; }
    public string? ChatTemplateKwargs { get; set; }
}
