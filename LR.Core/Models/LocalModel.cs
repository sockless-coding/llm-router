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
    /// Absolute path to the .gguf file on disk.
    /// </summary>
    [Required, MaxLength(1024)]
    public string FilePath { get; set; } = string.Empty;

    public long? FileSizeBytes { get; set; }

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

    /// <summary>
    /// Navigation: presets that reference this model.
    /// </summary>
    public ICollection<ModelPreset> Presets { get; set; } = new List<ModelPreset>();
}
