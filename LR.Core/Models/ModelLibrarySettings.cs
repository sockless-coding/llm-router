using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// Configuration for the model library — where models are imported from/downloaded to, and
/// optional Hugging Face credentials for gated repos / higher rate limits. Persisted as a
/// single-row table (edited from the Models page) rather than appsettings.json — it's a
/// UI-controlled setting the app itself writes at runtime, not deployment configuration.
/// </summary>
[Table("ModelLibrarySettings")]
public class ModelLibrarySettings
{
    /// <summary>
    /// Singleton row — this table only ever holds one settings record, with Id fixed at 1.
    /// </summary>
    [Key]
    public int Id { get; set; } = 1;

    /// <summary>
    /// Default folder used for "scan folder" imports and as the download destination for models
    /// fetched from Hugging Face.
    /// </summary>
    [MaxLength(1024)]
    public string RootFolder { get; set; } = string.Empty;

    /// <summary>
    /// Optional Hugging Face API token (bearer), used for gated/private repos and to avoid
    /// anonymous rate limits.
    /// </summary>
    [MaxLength(256)]
    public string? HuggingFaceApiToken { get; set; }
}
