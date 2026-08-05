using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// A named preset that defines how a server instance should load a model.
/// All properties are nullable — null means "use llama.cpp default".
/// </summary>
[Table("ModelPresets")]
public class ModelPreset
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The server instance this preset belongs to.
    /// </summary>
    [Required, ForeignKey(nameof(ServerInstance))]
    public Guid ServerInstanceId { get; set; }

    [Required, MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    // ==================== CORE (Always Visible) ====================

    /// <summary>
    /// Path to the model file (-m).
    /// </summary>
    [Required, MaxLength(1024)]
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>
    /// Prompt context size (-c). 0 = loaded from model.
    /// </summary>
    public int? ContextSize { get; set; }

    /// <summary>
    /// Max. number of layers to store in VRAM (-ngl). -1 = all, 0 = none.
    /// </summary>
    public int? GpuLayers { get; set; }

    /// <summary>
    /// KV cache data type for K (-ctk). f32/f16/bf16/q8_0/q4_0/q4_1/iq4_nl/q5_0/q5_1.
    /// </summary>
    [MaxLength(16)]
    public string? CacheTypeK { get; set; }

    /// <summary>
    /// KV cache data type for V (-ctv). f32/f16/bf16/q8_0/q4_0/q4_1/iq4_nl/q5_0/q5_1.
    /// </summary>
    [MaxLength(16)]
    public string? CacheTypeV { get; set; }

    /// <summary>
    /// Flash Attention use (-fa). on/off/auto.
    /// </summary>
    [MaxLength(4)]
    public string? FlashAttention { get; set; }

    /// <summary>
    /// Use jinja template engine for chat (--jinja).
    /// </summary>
    public bool? Jinja { get; set; }

    /// <summary>
    /// Speculative decoding types (--spec-type). Comma-separated: none,draft-simple,ngram-simple,etc.
    /// </summary>
    [MaxLength(256)]
    public string? SpecType { get; set; }

    // ==================== SAMPLING (Always Visible) ====================

    /// <summary>
    /// Temperature (--temp). Default: 0.8.
    /// </summary>
    public float? Temperature { get; set; }

    /// <summary>
    /// Top-K sampling (--top-k). 0 = disabled. Default: 40.
    /// </summary>
    public int? TopK { get; set; }

    /// <summary>
    /// Min-P sampling (--min-p). 0 = disabled. Default: 0.05.
    /// </summary>
    public float? MinP { get; set; }

    /// <summary>
    /// Top-P sampling (--top-p). 1.0 = disabled. Default: 0.95.
    /// </summary>
    public float? TopP { get; set; }

    /// <summary>
    /// Repeat penalty (--repeat-penalty). 1.0 = disabled. Default: 1.0.
    /// </summary>
    public float? RepeatPenalty { get; set; }

    /// <summary>
    /// Presence penalty (--presence-penalty). 0.0 = disabled.
    /// </summary>
    public float? PresencePenalty { get; set; }

    // ==================== ADVANCED: Generation ====================

    /// <summary>
    /// CPU threads for generation (-t).
    /// </summary>
    public int? Threads { get; set; }

    /// <summary>
    /// CPU threads for batch processing (-tb).
    /// </summary>
    public int? ThreadsBatch { get; set; }

    /// <summary>
    /// Number of tokens to predict (-n). -1 = infinity.
    /// </summary>
    public int? PredictN { get; set; }

    /// <summary>
    /// Logical max batch size (-b). Default: 2048.
    /// </summary>
    public int? BatchSize { get; set; }

    /// <summary>
    /// Physical max batch size (-ub). Default: 512.
    /// </summary>
    public int? UbatchSize { get; set; }

    /// <summary>
    /// Tokens to keep from initial prompt (--keep). -1 = all.
    /// </summary>
    public int? KeepN { get; set; }

    /// <summary>
    /// RNG seed (-s). -1 = random.
    /// </summary>
    public long? Seed { get; set; }

    /// <summary>
    /// Ignore EOS token (--ignore-eos).
    /// </summary>
    public bool? IgnoreEos { get; set; }

    // ==================== ADVANCED: GPU/Device ====================

    /// <summary>
    /// Comma-separated list of devices (-dev).
    /// </summary>
    [MaxLength(512)]
    public string? Device { get; set; }

    /// <summary>
    /// Split mode across GPUs (-sm). none/layer/row/tensor.
    /// </summary>
    [MaxLength(8)]
    public string? SplitMode { get; set; }

    /// <summary>
    /// Tensor split proportions (-ts). e.g. 3,1.
    /// </summary>
    [MaxLength(256)]
    public string? TensorSplit { get; set; }

    /// <summary>
    /// Main GPU index (-mg).
    /// </summary>
    public int? MainGpu { get; set; }

    /// <summary>
    /// Fit to device memory (-fit). on/off.
    /// </summary>
    [MaxLength(3)]
    public string? Fit { get; set; }

    /// <summary>
    /// KV cache offloading (-kvo).
    /// </summary>
    public bool? KvOffload { get; set; }

    /// <summary>
    /// Weight repacking (--repack).
    /// </summary>
    public bool? Repack { get; set; }

    // ==================== ADVANCED: Memory ====================

    /// <summary>
    /// Model loading mode (-lm). none/mmap/mlock/dio.
    /// </summary>
    [MaxLength(16)]
    public string? LoadMode { get; set; }

    /// <summary>
    /// Max cache size in MiB (-cram). -1 = no limit, 0 = disable.
    /// </summary>
    public int? CacheRam { get; set; }

    // ==================== ADVANCED: RoPE Scaling ====================

    /// <summary>
    /// RoPE scaling method (--rope-scaling). none/linear/yarn.
    /// </summary>
    [MaxLength(8)]
    public string? RopeScalingType { get; set; }

    /// <summary>
    /// RoPE context scaling factor (--rope-scale).
    /// </summary>
    public float? RopeScale { get; set; }

    /// <summary>
    /// RoPE base frequency (--rope-freq-base).
    /// </summary>
    public float? RopeFreqBase { get; set; }

    /// <summary>
    /// RoPE frequency scaling factor (--rope-freq-scale).
    /// </summary>
    public float? RopeFreqScale { get; set; }

    /// <summary>
    /// YaRN original context size (--yarn-orig-ctx).
    /// </summary>
    public int? YarnOrigCtx { get; set; }

    /// <summary>
    /// YaRN extrapolation mix factor (--yarn-ext-factor).
    /// </summary>
    public float? YarnExtFactor { get; set; }

    /// <summary>
    /// YaRN attention magnitude (--yarn-attn-factor).
    /// </summary>
    public float? YarnAttnFactor { get; set; }

    /// <summary>
    /// YaRN high correction dim (--yarn-beta-slow).
    /// </summary>
    public float? YarnBetaSlow { get; set; }

    /// <summary>
    /// YaRN low correction dim (--yarn-beta-fast).
    /// </summary>
    public float? YarnBetaFast { get; set; }

    // ==================== ADVANCED: Sampling (Extended) ====================

    /// <summary>
    /// Top-N-Sigma sampling (--top-nsigma). -1 = disabled.
    /// </summary>
    public float? TopNSigma { get; set; }

    /// <summary>
    /// XTC probability (--xtc-probability). 0 = disabled.
    /// </summary>
    public float? XtcProbability { get; set; }

    /// <summary>
    /// XTC threshold (--xtc-threshold).
    /// </summary>
    public float? XtcThreshold { get; set; }

    /// <summary>
    /// Locally typical sampling (--typical-p). 1.0 = disabled.
    /// </summary>
    public float? TypicalP { get; set; }

    /// <summary>
    /// Last n tokens for repeat penalty (--repeat-last-n). -1 = ctx_size, 0 = disabled.
    /// </summary>
    public int? RepeatLastN { get; set; }

    /// <summary>
    /// Frequency penalty (--frequency-penalty).
    /// </summary>
    public float? FrequencyPenalty { get; set; }

    /// <summary>
    /// DRY multiplier (--dry-multiplier). 0 = disabled.
    /// </summary>
    public float? DryMultiplier { get; set; }

    /// <summary>
    /// DRY base value (--dry-base).
    /// </summary>
    public float? DryBase { get; set; }

    /// <summary>
    /// DRY allowed length (--dry-allowed-length).
    /// </summary>
    public int? DryAllowedLength { get; set; }

    /// <summary>
    /// DRY penalty last n (--dry-penalty-last-n). -1 = context size.
    /// </summary>
    public int? DryPenaltyLastN { get; set; }

    /// <summary>
    /// Mirostat mode (--mirostat). 0=disabled, 1=Mirostat, 2=Mirostat 2.0.
    /// </summary>
    public int? Mirostat { get; set; }

    /// <summary>
    /// Mirostat learning rate (--mirostat-lr).
    /// </summary>
    public float? MirostatTau { get; set; }

    /// <summary>
    /// Mirostat target entropy (--mirostat-ent).
    /// </summary>
    public float? MirostatEta { get; set; }

    /// <summary>
    /// Dynamic temperature range (--dynatemp-range). 0 = disabled.
    /// </summary>
    public float? DynatempRange { get; set; }

    /// <summary>
    /// Dynamic temperature exponent (--dynatemp-exp).
    /// </summary>
    public float? DynatempExp { get; set; }

    // ==================== ADVANCED: Speculative Decoding ====================

    /// <summary>
    /// Draft model path for speculative decoding (--spec-draft-model).
    /// </summary>
    [MaxLength(1024)]
    public string? SpecDraftModel { get; set; }

    /// <summary>
    /// Max draft tokens (--spec-draft-n-max). Default: 3.
    /// </summary>
    public int? SpecDraftNMax { get; set; }

    /// <summary>
    /// Min draft tokens (--spec-draft-n-min).
    /// </summary>
    public int? SpecDraftNMin { get; set; }

    /// <summary>
    /// Min speculative probability (--draft-p-min).
    /// </summary>
    public float? SpecDraftPMin { get; set; }

    /// <summary>
    /// KV cache type for K in draft model.
    /// </summary>
    [MaxLength(16)]
    public string? SpecDraftTypeK { get; set; }

    /// <summary>
    /// KV cache type for V in draft model.
    /// </summary>
    [MaxLength(16)]
    public string? SpecDraftTypeV { get; set; }

    /// <summary>
    /// GPU layers for draft model (--spec-draft-ngl).
    /// </summary>
    public int? SpecDraftGpuLayers { get; set; }

    /// <summary>
    /// CPU threads for draft model.
    /// </summary>
    public int? SpecDraftThreads { get; set; }

    // ==================== ADVANCED: Server ====================

    /// <summary>
    /// Host to listen on (--host). Default: 127.0.0.1.
    /// </summary>
    [MaxLength(64)]
    public string? Host { get; set; }

    /// <summary>
    /// Port to listen on (--port).
    /// </summary>
    public int? Port { get; set; }

    /// <summary>
    /// Number of server slots (-np). -1 = auto.
    /// </summary>
    public int? Parallel { get; set; }

    /// <summary>
    /// Continuous batching (-cb).
    /// </summary>
    public bool? ContBatching { get; set; }

    /// <summary>
    /// Server timeout in seconds (--timeout). Default: 3600.
    /// </summary>
    public int? Timeout { get; set; }

    /// <summary>
    /// API key for authentication (--api-key).
    /// </summary>
    [MaxLength(512)]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Enable prompt caching (--cache-prompt).
    /// </summary>
    public bool? CachePrompt { get; set; }

    // ==================== ADVANCED: Reasoning ====================

    /// <summary>
    /// Reasoning mode (-rea). on/off/auto.
    /// </summary>
    [MaxLength(4)]
    public string? Reasoning { get; set; }

    /// <summary>
    /// Token budget for reasoning (--reasoning-budget). -1 = unrestricted, 0 = disabled.
    /// </summary>
    public int? ReasoningBudget { get; set; }

    // ==================== ADVANCED: Multimodal ====================

    /// <summary>
    /// Path to multimodal projector file (-mm).
    /// </summary>
    [MaxLength(1024)]
    public string? Mmproj { get; set; }

    /// <summary>
    /// Min tokens per image (--image-min-tokens).
    /// </summary>
    public int? ImageMinTokens { get; set; }

    /// <summary>
    /// Max tokens per image (--image-max-tokens).
    /// </summary>
    public int? ImageMaxTokens { get; set; }

    // ==================== ADVANCED: LoRA ====================

    /// <summary>
    /// Comma-separated LoRA adapter paths (--lora).
    /// </summary>
    [MaxLength(2048)]
    public string? Lora { get; set; }

    // ==================== ADVANCED: Chat Template ====================

    /// <summary>
    /// Custom jinja chat template name or definition (--chat-template).
    /// </summary>
    [MaxLength(2048)]
    public string? ChatTemplate { get; set; }

    // ==================== GGUF METADATA (Auto-Read from File) ====================

    /// <summary>
    /// Architecture name read from GGUF file (e.g. "llama", "gemma2", "phi3").
    /// </summary>
    [MaxLength(64)]
    public string? GgufArchitecture { get; set; }

    /// <summary>
    /// Model name read from GGUF file (e.g. "Llama-2-7B-chat").
    /// </summary>
    [MaxLength(256)]
    public string? GgufModelName { get; set; }

    /// <summary>
    /// Human-readable parameter size from GGUF file (e.g. "7B", "13B").
    /// </summary>
    [MaxLength(32)]
    public string? GgufParameterSize { get; set; }

    /// <summary>
    /// Quantization level read from GGUF file (e.g. "Q4_K_M", "Q8_0").
    /// </summary>
    [MaxLength(16)]
    public string? GgufQuantizationLevel { get; set; }

    /// <summary>
    /// Context length read from GGUF file (e.g. 4096, 8192).
    /// </summary>
    public int? GgufContextLength { get; set; }

    /// <summary>
    /// Embedding length read from GGUF file (e.g. 4096, 3072).
    /// </summary>
    public int? GgufEmbeddingLength { get; set; }

    /// <summary>
    /// RoPE base frequency read from GGUF file.
    /// </summary>
    public double? GgufRopeFreqBase { get; set; }

    /// <summary>
    /// Chat template string read from GGUF file (tokenizer.chat_template).
    /// </summary>
    [MaxLength(4096)]
    public string? GgufChatTemplate { get; set; }

    // ==================== LEGACY / FALLBACK ====================

    /// <summary>
    /// Free-form backend flags (fallback for options without dedicated properties).
    /// Stored as JSON in the database.
    /// </summary>
    [Column(TypeName = "TEXT")]
    public Dictionary<string, string> Flags { get; set; } = new();

    // ==================== NAVIGATION ====================

    /// <summary>
    /// Navigation: parent server instance.
    /// </summary>
    public ServerInstance? ServerInstance { get; set; }
}
