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
    /// Path to the model file (-m). When <see cref="ModelId"/> is set, this is kept in sync with
    /// the linked <see cref="LocalModel.FilePath"/>; when null, this is a manually-typed override.
    /// This is what <c>LlamaCppArgBuilder</c> actually consumes — it's unaffected by whether the
    /// path came from the registry or a manual entry.
    /// </summary>
    [Required, MaxLength(1024)]
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>
    /// Optional link to a registered model in the model library (see <see cref="LocalModel"/>).
    /// Null means <see cref="ModelPath"/> was entered manually and isn't tracked in the registry.
    /// </summary>
    public Guid? ModelId { get; set; }

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

    /// <summary>
    /// Keep all Mixture-of-Experts weights on the CPU (--cpu-moe). Mutually exclusive in practice
    /// with <see cref="NCpuMoe"/> — pick one.
    /// </summary>
    public bool? CpuMoe { get; set; }

    /// <summary>
    /// Keep the MoE weights of the first N layers on the CPU (--n-cpu-moe).
    /// </summary>
    public int? NCpuMoe { get; set; }

    /// <summary>
    /// Override tensor buffer type, e.g. "exps=CPU" (--override-tensor). Comma-separated for
    /// multiple overrides.
    /// </summary>
    [MaxLength(1024)]
    public string? OverrideTensor { get; set; }

    /// <summary>
    /// Target free-memory margin per device for --fit, in MiB, comma-separated per device
    /// (--fit-target). Default: 1024.
    /// </summary>
    [MaxLength(256)]
    public string? FitTarget { get; set; }

    /// <summary>
    /// Minimum context size --fit is allowed to shrink to (--fit-ctx). Default: 4096.
    /// </summary>
    public int? FitCtx { get; set; }

    // ==================== ADVANCED: Memory ====================

    /// <summary>
    /// Model loading mode (-lm). auto/none/mmap/mlock/mmap+mlock/dio. Default: auto.
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
    /// Semicolon-separated sampler order (--samplers). Default:
    /// penalties;dry;top_n_sigma;top_k;typ_p;top_p;min_p;xtc;temperature.
    /// </summary>
    [MaxLength(256)]
    public string? Samplers { get; set; }

    /// <summary>
    /// Simplified sampler sequence (--sampler-seq). Default: edskypmxt.
    /// </summary>
    [MaxLength(32)]
    public string? SamplerSeq { get; set; }

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
    /// Comma-separated DRY sequence breakers (--dry-sequence-breaker, repeated once per breaker).
    /// Setting this clears llama.cpp's default breakers ('\n', ':', '"', '*'). Use "none" to
    /// disable sequence breakers entirely.
    /// </summary>
    [MaxLength(256)]
    public string? DrySequenceBreaker { get; set; }

    /// <summary>
    /// Mirostat mode (--mirostat). 0=disabled, 1=Mirostat, 2=Mirostat 2.0.
    /// </summary>
    public int? Mirostat { get; set; }

    /// <summary>
    /// Mirostat target entropy, parameter tau (--mirostat-ent). Default: 5.00.
    /// </summary>
    public float? MirostatTau { get; set; }

    /// <summary>
    /// Mirostat learning rate, parameter eta (--mirostat-lr). Default: 0.10.
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
    /// Optional link to a registered draft model in the model library. When set, this is kept in
    /// sync with the linked <see cref="LocalModel.FilePath"/>, mirroring how <see cref="ModelId"/>
    /// drives <see cref="ModelPath"/>. Null means <see cref="SpecDraftModel"/> was entered manually.
    /// </summary>
    public Guid? SpecDraftModelId { get; set; }

    /// <summary>
    /// Draft model path for speculative decoding (--spec-draft-model). When <see cref="SpecDraftModelId"/>
    /// is set, this is kept in sync with the linked model's file path — see
    /// <c>PresetManager.ApplyLinkedDraftModelAsync</c>.
    /// </summary>
    [MaxLength(1024)]
    public string? SpecDraftModel { get; set; }

    /// <summary>
    /// Draft model via a Hugging Face repo, as an alternative to a local <see cref="SpecDraftModel"/>
    /// path (--spec-draft-hf / -hfd).
    /// </summary>
    [MaxLength(256)]
    public string? SpecDraftHfRepo { get; set; }

    /// <summary>
    /// Comma-separated list of devices to run the draft model on (--spec-draft-device).
    /// </summary>
    [MaxLength(512)]
    public string? SpecDraftDevice { get; set; }

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
    /// Speculative decoding split probability (--spec-draft-p-split). Default: 0.10.
    /// </summary>
    public float? SpecDraftPSplit { get; set; }

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

    /// <summary>
    /// Min chunk size to attempt reusing from the prompt cache via KV shifting (--cache-reuse).
    /// Requires prompt caching to be enabled. Default: 0 (disabled).
    /// </summary>
    public int? CacheReuse { get; set; }

    /// <summary>
    /// Use context shift on infinite text generation (--context-shift / --no-context-shift).
    /// Default: disabled.
    /// </summary>
    public bool? ContextShift { get; set; }

    /// <summary>
    /// Use a single unified KV buffer shared across all sequences (--kv-unified /
    /// --no-kv-unified). Default: enabled when the number of slots is auto.
    /// </summary>
    public bool? KvUnified { get; set; }

    /// <summary>
    /// How much a request's prompt must match a slot's prompt to reuse that slot
    /// (--slot-prompt-similarity). Default: 0.10, 0.0 = disabled.
    /// </summary>
    public float? SlotPromptSimilarity { get; set; }

    /// <summary>
    /// Seconds of idleness after which the server will sleep (--sleep-idle-seconds).
    /// Default: -1 (disabled).
    /// </summary>
    public int? SleepIdleSeconds { get; set; }

    // ==================== ADVANCED: Reasoning ====================

    /// <summary>
    /// Reasoning mode (-rea). on/off/auto.
    /// </summary>
    [MaxLength(4)]
    public string? Reasoning { get; set; }

    /// <summary>
    /// Reasoning effort level given to the chat template (--reasoning-effort). One of: default,
    /// minimal, low, medium, high, xhigh, max.
    /// </summary>
    [MaxLength(16)]
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Token budget for reasoning (--reasoning-budget). -1 = unrestricted, 0 = disabled.
    /// </summary>
    public int? ReasoningBudget { get; set; }

    /// <summary>
    /// Message injected before the end-of-thinking tag when the reasoning budget is exhausted
    /// (--reasoning-budget-message).
    /// </summary>
    [MaxLength(512)]
    public string? ReasoningBudgetMessage { get; set; }

    /// <summary>
    /// Controls how thought tags are extracted from the response (--reasoning-format).
    /// One of: none, deepseek, deepseek-legacy. Empty/null leaves llama.cpp's "auto" default,
    /// which guesses from the model's chat template and can misclassify non-standard reasoning
    /// output (e.g. everything ending up in reasoning_content and nothing in content).
    /// </summary>
    [MaxLength(16)]
    public string? ReasoningFormat { get; set; }

    /// <summary>
    /// Preserve the reasoning trace across the full conversation history instead of just the
    /// last turn (--reasoning-preserve / --no-reasoning-preserve). Null leaves llama.cpp's
    /// template-default behavior.
    /// </summary>
    public bool? ReasoningPreserve { get; set; }

    // ==================== ADVANCED: Multimodal ====================

    /// <summary>
    /// Optional link to a registered multimodal projector in the model library. When set, this is
    /// kept in sync with the linked <see cref="LocalModel.FilePath"/>, mirroring how
    /// <see cref="ModelId"/> drives <see cref="ModelPath"/>. Null means <see cref="Mmproj"/> was
    /// either entered manually or auto-filled from the main model's detected sibling projector.
    /// </summary>
    public Guid? MmprojId { get; set; }

    /// <summary>
    /// Path to multimodal projector file (-mm). When <see cref="MmprojId"/> is set, this is kept in
    /// sync with the linked model's file path — see <c>PresetManager.ApplyLinkedMmprojAsync</c>.
    /// </summary>
    [MaxLength(1024)]
    public string? Mmproj { get; set; }

    /// <summary>
    /// URL to a multimodal projector file (--mmproj-url).
    /// </summary>
    [MaxLength(1024)]
    public string? MmprojUrl { get; set; }

    /// <summary>
    /// Use the multimodal projector file automatically when available, e.g. with -hf
    /// (--mmproj-auto / --no-mmproj-auto). Default: enabled.
    /// </summary>
    public bool? MmprojAuto { get; set; }

    /// <summary>
    /// Enable GPU offloading for the multimodal projector (--mmproj-offload /
    /// --no-mmproj-offload). Default: enabled.
    /// </summary>
    public bool? MmprojOffload { get; set; }

    /// <summary>
    /// Device to use for the multimodal projector, none = don't offload (--mmproj-device).
    /// Default: auto.
    /// </summary>
    [MaxLength(256)]
    public string? MmprojDevice { get; set; }

    /// <summary>
    /// Min tokens per image (--image-min-tokens).
    /// </summary>
    public int? ImageMinTokens { get; set; }

    /// <summary>
    /// Max tokens per image (--image-max-tokens).
    /// </summary>
    public int? ImageMaxTokens { get; set; }

    /// <summary>
    /// Max image tokens per batch when encoding images (--mtmd-batch-max-tokens). Default: 1024.
    /// </summary>
    public int? MtmdBatchMaxTokens { get; set; }

    /// <summary>
    /// Video frames sampled per second when encoding video input (--video-fps).
    /// </summary>
    public float? VideoFps { get; set; }

    /// <summary>
    /// Interval in ms between sampled video frame timestamps (--video-timestamp-interval).
    /// </summary>
    public int? VideoTimestampInterval { get; set; }

    /// <summary>
    /// Path to an ffmpeg installation used for video decoding (--video-ffmpeg-dir).
    /// </summary>
    [MaxLength(1024)]
    public string? VideoFfmpegDir { get; set; }

    // ==================== ADVANCED: LoRA ====================

    /// <summary>
    /// Comma-separated LoRA adapter paths (--lora).
    /// </summary>
    [MaxLength(2048)]
    public string? Lora { get; set; }

    /// <summary>
    /// Comma-separated LoRA adapters with user-defined scaling, format FNAME:SCALE,...
    /// (--lora-scaled).
    /// </summary>
    [MaxLength(2048)]
    public string? LoraScaled { get; set; }

    /// <summary>
    /// Comma-separated control vector paths to add (--control-vector).
    /// </summary>
    [MaxLength(2048)]
    public string? ControlVector { get; set; }

    /// <summary>
    /// Comma-separated control vectors with user-defined scaling, format FNAME:SCALE,...
    /// (--control-vector-scaled).
    /// </summary>
    [MaxLength(2048)]
    public string? ControlVectorScaled { get; set; }

    /// <summary>
    /// Start of the layer range the control vector(s) apply to, inclusive
    /// (--control-vector-layer-range START END). Requires <see cref="ControlVectorLayerEnd"/>.
    /// </summary>
    public int? ControlVectorLayerStart { get; set; }

    /// <summary>
    /// End of the layer range the control vector(s) apply to, inclusive
    /// (--control-vector-layer-range START END). Requires <see cref="ControlVectorLayerStart"/>.
    /// </summary>
    public int? ControlVectorLayerEnd { get; set; }

    /// <summary>
    /// Load LoRA adapters without applying them, so they can be toggled per-request via the API
    /// instead of always-on (--lora-init-without-apply).
    /// </summary>
    public bool? LoraInitWithoutApply { get; set; }

    // ==================== ADVANCED: Server Behavior & Endpoints ====================

    /// <summary>
    /// Model name alias exposed via the API (-a, --alias).
    /// </summary>
    [MaxLength(256)]
    public string? ModelAlias { get; set; }

    /// <summary>
    /// Comma-separated model tags (--tags).
    /// </summary>
    [MaxLength(512)]
    public string? ModelTags { get; set; }

    /// <summary>
    /// Restrict the server to embeddings-only mode (--embeddings / --no-embeddings).
    /// </summary>
    public bool? Embeddings { get; set; }

    /// <summary>
    /// Enable the rerank endpoint (--reranking / --no-reranking).
    /// </summary>
    public bool? Reranking { get; set; }

    /// <summary>
    /// Pooling type for embeddings (--pooling). none/mean/cls/last/rank.
    /// </summary>
    [MaxLength(8)]
    public string? Pooling { get; set; }

    /// <summary>
    /// Embeddings normalization method (--embd-normalize).
    /// </summary>
    public int? EmbdNormalize { get; set; }

    /// <summary>
    /// Enable the /slots monitoring endpoint (--slots / --no-slots).
    /// </summary>
    public bool? SlotsEndpoint { get; set; }

    /// <summary>
    /// Enable the Prometheus /metrics endpoint (--metrics / --no-metrics).
    /// </summary>
    public bool? MetricsEndpoint { get; set; }

    /// <summary>
    /// Allow changing global properties via POST /props (--props / --no-props).
    /// </summary>
    public bool? PropsEndpoint { get; set; }

    /// <summary>
    /// Enable the built-in web UI (--webui / --no-webui).
    /// </summary>
    public bool? WebUi { get; set; }

    /// <summary>
    /// Path prefix the server serves its API under (--api-prefix).
    /// </summary>
    [MaxLength(128)]
    public string? ApiPrefix { get; set; }

    /// <summary>
    /// Number of HTTP worker threads (--threads-http).
    /// </summary>
    public int? ThreadsHttp { get; set; }

    /// <summary>
    /// Server-sent events keep-alive interval in seconds (--sse-ping-interval).
    /// </summary>
    public int? SsePingInterval { get; set; }

    /// <summary>
    /// Allow multiple sockets to bind the same port (--reuse-port).
    /// </summary>
    public bool? ReusePort { get; set; }

    /// <summary>
    /// Directory to persist/restore slot KV caches (--slot-save-path).
    /// </summary>
    [MaxLength(1024)]
    public string? SlotSavePath { get; set; }

    /// <summary>
    /// Directory containing local media files servable to the model (--media-path).
    /// </summary>
    [MaxLength(1024)]
    public string? MediaPath { get; set; }

    /// <summary>
    /// Use a full-size sliding-window-attention cache instead of the smaller default (--swa-full).
    /// </summary>
    public bool? SwaFull { get; set; }

    /// <summary>
    /// Persist idle slots' KV cache to disk instead of dropping it (--cache-idle-slots /
    /// --no-cache-idle-slots).
    /// </summary>
    public bool? CacheIdleSlots { get; set; }

    /// <summary>
    /// Per-slot context size limit when using a unified KV buffer (--kv-unified-per-slot).
    /// </summary>
    public int? KvUnifiedPerSlot { get; set; }

    /// <summary>
    /// Max context checkpoints kept per slot (-ctxcp, --ctx-checkpoints).
    /// </summary>
    public int? CtxCheckpoints { get; set; }

    /// <summary>
    /// Run a warmup pass before accepting requests (--warmup / --no-warmup).
    /// </summary>
    public bool? Warmup { get; set; }

    /// <summary>
    /// Use the suffix/prefix/middle infill token pattern instead of prefix/suffix/middle
    /// (--spm-infill).
    /// </summary>
    public bool? SpmInfill { get; set; }

    /// <summary>
    /// Force the raw content parser instead of the model-specific chat parser (--skip-chat-parsing
    /// / --no-skip-chat-parsing).
    /// </summary>
    public bool? SkipChatParsing { get; set; }

    /// <summary>
    /// Prefill the assistant turn from the last message when it has the assistant role
    /// (--prefill-assistant / --no-prefill-assistant).
    /// </summary>
    public bool? PrefillAssistant { get; set; }

    /// <summary>
    /// NUMA optimization mode (--numa). distribute/isolate/numactl.
    /// </summary>
    [MaxLength(16)]
    public string? Numa { get; set; }

    /// <summary>
    /// Comma-separated list of RPC backend servers (--rpc).
    /// </summary>
    [MaxLength(1024)]
    public string? RpcServers { get; set; }

    /// <summary>
    /// Comma-separated model metadata overrides, format KEY=TYPE:VALUE (--override-kv, repeated
    /// once per entry).
    /// </summary>
    [MaxLength(1024)]
    public string? OverrideKv { get; set; }

    // ==================== ADVANCED: Security & CORS ====================

    /// <summary>
    /// Allowed CORS origins (--cors-origins).
    /// </summary>
    [MaxLength(1024)]
    public string? CorsOrigins { get; set; }

    /// <summary>
    /// Allowed CORS methods (--cors-methods).
    /// </summary>
    [MaxLength(256)]
    public string? CorsMethods { get; set; }

    /// <summary>
    /// Allowed CORS headers (--cors-headers).
    /// </summary>
    [MaxLength(256)]
    public string? CorsHeaders { get; set; }

    /// <summary>
    /// Allow CORS credentials (--cors-credentials / --no-cors-credentials).
    /// </summary>
    public bool? CorsCredentials { get; set; }

    /// <summary>
    /// Path to a file of API keys, one per line (--api-key-file).
    /// </summary>
    [MaxLength(1024)]
    public string? ApiKeyFile { get; set; }

    /// <summary>
    /// Path to a PEM SSL private key file, to serve over HTTPS (--ssl-key-file).
    /// </summary>
    [MaxLength(1024)]
    public string? SslKeyFile { get; set; }

    /// <summary>
    /// Path to a PEM SSL certificate file, to serve over HTTPS (--ssl-cert-file).
    /// </summary>
    [MaxLength(1024)]
    public string? SslCertFile { get; set; }

    // ==================== ADVANCED: Logging ====================

    /// <summary>
    /// Disable logging entirely (--log-disable).
    /// </summary>
    public bool? LogDisable { get; set; }

    /// <summary>
    /// Path to log to a file instead of stdout (--log-file).
    /// </summary>
    [MaxLength(1024)]
    public string? LogFile { get; set; }

    /// <summary>
    /// Log verbosity threshold, 0-5 (-lv, --log-verbosity).
    /// </summary>
    public int? LogVerbosity { get; set; }

    /// <summary>
    /// Colored log output (--log-colors). on/off/auto.
    /// </summary>
    [MaxLength(4)]
    public string? LogColors { get; set; }

    /// <summary>
    /// Include timestamps in log output (--log-timestamps / --no-log-timestamps).
    /// </summary>
    public bool? LogTimestamps { get; set; }

    /// <summary>
    /// Include prefixes in log output (--log-prefix / --no-log-prefix).
    /// </summary>
    public bool? LogPrefix { get; set; }

    /// <summary>
    /// Emit logs as JSONL instead of plain text (--log-jsonl / --no-log-jsonl).
    /// </summary>
    public bool? LogJsonl { get; set; }

    /// <summary>
    /// Directory to log prompts/completions to (--log-prompts-dir).
    /// </summary>
    [MaxLength(1024)]
    public string? LogPromptsDir { get; set; }

    // ==================== ADVANCED: Chat Template ====================

    /// <summary>
    /// Custom jinja chat template name or definition (--chat-template).
    /// </summary>
    [MaxLength(2048)]
    public string? ChatTemplate { get; set; }

    /// <summary>
    /// JSON object of extra kwargs passed into the chat template's Jinja render context, e.g.
    /// {"preserve_thinking": true} (--chat-template-kwargs). Distinct from <see cref="ChatTemplate"/>,
    /// which replaces the template itself rather than parameterizing it.
    /// </summary>
    [MaxLength(2048)]
    public string? ChatTemplateKwargs { get; set; }

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

    /// <summary>
    /// Navigation: the registry model this preset resolves its path/GGUF metadata from, if any.
    /// </summary>
    public LocalModel? Model { get; set; }

    /// <summary>
    /// Navigation: the registry model this preset resolves <see cref="SpecDraftModel"/> from,
    /// if linked via <see cref="SpecDraftModelId"/>.
    /// </summary>
    public LocalModel? SpecDraftModelRef { get; set; }

    /// <summary>
    /// Navigation: the registry model this preset resolves <see cref="Mmproj"/> from, if linked
    /// via <see cref="MmprojId"/>.
    /// </summary>
    public LocalModel? MmprojModelRef { get; set; }
}
