using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// A named preset that defines how a server instance should load a model.
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

    [Required, MaxLength(1024)]
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>
    /// Context length (e.g., 4096, 8192).
    /// </summary>
    public int ContextLength { get; set; }

    /// <summary>
    /// Number of layers to offload to GPU (-1 = all).
    /// </summary>
    public int GpuLayers { get; set; } = -1;

    /// <summary>
    /// Free-form backend flags (e.g., "--mlock", "--gpu-split").
    /// Stored as JSON in the database.
    /// </summary>
    [Column(TypeName = "TEXT")]
    public Dictionary<string, string> Flags { get; set; } = new();

    /// <summary>
    /// Navigation: parent server instance.
    /// </summary>
    public ServerInstance? ServerInstance { get; set; }
}
