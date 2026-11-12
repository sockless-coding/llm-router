using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// Where a <see cref="LocalModel"/>'s file came from.
/// </summary>
public enum ModelSource
{
    Local = 0,
    HuggingFace = 1
}

/// <summary>
/// What a <see cref="LocalModel"/>'s <see cref="LocalModel.FilePath"/> points at, which decides the
/// engines that can run it (<see cref="Services.ModelEngineSupport"/>). Values are persisted.
/// </summary>
public enum ModelFormat
{
    /// <summary>A .gguf file (a split model's first shard): llama.cpp, Strata, stable-diffusion.cpp.</summary>
    Gguf = 0,

    /// <summary>A single .safetensors file — a diffusion checkpoint or component for stable-diffusion.cpp.</summary>
    Safetensors = 1,

    /// <summary>
    /// A Hugging Face checkpoint folder (config.json, tokenizer, safetensors shards), as TensorFold and
    /// vLLM load it. <see cref="LocalModel.FilePath"/> is the folder.
    /// </summary>
    Checkpoint = 2,
}

/// <summary>
/// Lifecycle status of a <see cref="LocalModel"/>'s underlying file.
/// </summary>
public enum ModelStatus
{
    Ready = 0,
    Downloading = 1,
    Error = 2,
    Missing = 3
}

/// <summary>
/// A model file registered in the model library — the single source of truth for "what models do
/// I have", independent of any <see cref="ModelPreset"/> that launches one of them. GGUF metadata
/// is read once here (via <see cref="Interfaces.IGgufMetadataReader"/>) instead of being duplicated
/// per preset.
/// </summary>
[Table("LocalModels")]
public class LocalModel
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path to the model file on disk (.gguf, .safetensors), or to the folder of a
    /// <see cref="ModelFormat.Checkpoint"/>.
    /// </summary>
    [Required, MaxLength(1024)]
    public string FilePath { get; set; } = string.Empty;

    public long? FileSizeBytes { get; set; }

    /// <summary>What <see cref="FilePath"/> is: a GGUF or safetensors file, or a checkpoint folder.</summary>
    public ModelFormat Format { get; set; } = ModelFormat.Gguf;

    public ModelSource Source { get; set; } = ModelSource.Local;

    public ModelStatus Status { get; set; } = ModelStatus.Ready;

    /// <summary>
    /// Free-form error detail when <see cref="Status"/> is <see cref="ModelStatus.Error"/>.
    /// </summary>
    [MaxLength(2048)]
    public string? StatusMessage { get; set; }

    // ==================== HUGGING FACE SOURCE ====================

    [MaxLength(256)]
    public string? HfRepoId { get; set; }

    [MaxLength(512)]
    public string? HfFilename { get; set; }

    /// <summary>
    /// The commit SHA (or branch/tag) this file was fetched at — compared against the repo's
    /// latest revision to detect updates.
    /// </summary>
    [MaxLength(64)]
    public string? HfRevision { get; set; }

    // ==================== GGUF METADATA (Auto-Read from File) ====================

    [MaxLength(64)]
    public string? Architecture { get; set; }

    [MaxLength(256)]
    public string? GgufModelName { get; set; }

    [MaxLength(32)]
    public string? ParameterSize { get; set; }

    [MaxLength(16)]
    public string? QuantizationLevel { get; set; }

    public int? ContextLength { get; set; }
    public int? EmbeddingLength { get; set; }
    public int? FeedForwardLength { get; set; }
    public int? BlockCount { get; set; }
    public int? HeadCount { get; set; }
    public int? KvHeadCount { get; set; }
    public double? RopeFreqBase { get; set; }
    public int? EosTokenId { get; set; }
    public int? BosTokenId { get; set; }

    [Column(TypeName = "TEXT")]
    public string? ChatTemplate { get; set; }

    /// <summary>
    /// {arch}.decision.type — non-null marks a native decision model, served through
    /// <c>/v1/systemone</c> (see <see cref="Services.ModelKindClassifier"/>).
    /// </summary>
    [MaxLength(64)]
    public string? DecisionType { get; set; }

    /// <summary>
    /// {arch}.pooling_type — present on embedding (1–3) and reranking (4) models only.
    /// </summary>
    public int? PoolingType { get; set; }

    [Column(TypeName = "TEXT")]
    public string? LicenseText { get; set; }

    /// <summary>
    /// Raw GGUF key-value pairs, JSON-serialized, for the Details page. Excludes large
    /// tokenizer arrays (see <see cref="Services.GgufMetadataReader"/>).
    /// </summary>
    [Column(TypeName = "TEXT")]
    public string? AllKvPairsJson { get; set; }

    /// <summary>
    /// Path to a multimodal projector (mmproj) .gguf file found next to this model's file at
    /// scan/refresh time (see <see cref="Services.MmprojLocator"/>). Vision models are usually
    /// converted to GGUF as two separate files — the text backbone and the projector — sitting
    /// in the same folder, so this lets presets pick up the projector without manual wiring.
    /// </summary>
    [MaxLength(1024)]
    public string? DetectedMmprojPath { get; set; }

    // ==================== MISC ====================

    [MaxLength(2048)]
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastVerifiedAt { get; set; }

    // ==================== LOCATION ====================

    /// <summary>
    /// The remote agent whose disk holds this file (<see cref="FilePath"/> is a path on that
    /// machine), or null for this router's machine. Presets of servers on an agent pick from that
    /// agent's models.
    /// </summary>
    public Guid? AgentId { get; set; }

    /// <summary>Navigation: the agent this file lives on, if any.</summary>
    [ForeignKey(nameof(AgentId))]
    public RemoteAgent? Agent { get; set; }

    /// <summary>
    /// Navigation: presets that reference this model.
    /// </summary>
    public ICollection<ModelPreset> Presets { get; set; } = new List<ModelPreset>();
}
