using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// Represents a managed inference server instance.
/// </summary>
[Table("ServerInstances")]
public class ServerInstance
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    public BackendType BackendType { get; set; }
    public ServerStatus Status { get; set; }
    public bool IsHealthy { get; set; }

    /// <summary>
    /// The ID of the currently active preset, if any.
    /// </summary>
    public Guid? ActivePresetId { get; set; }

    /// <summary>
    /// Base URL where the server is listening (set after start).
    /// </summary>
    [MaxLength(512)]
    public string? Url { get; set; }

    /// <summary>
    /// Optional port override for the backend process.
    /// </summary>
    public int? Port { get; set; }

    /// <summary>
    /// Navigation: presets belonging to this server instance.
    /// </summary>
    [ForeignKey(nameof(ModelPreset.ServerInstanceId))]
    public ICollection<ModelPreset> Presets { get; set; } = new List<ModelPreset>();

    /// <summary>
    /// Navigation: the currently active preset.
    /// </summary>
    [ForeignKey(nameof(ActivePresetId))]
    public ModelPreset? ActivePreset { get; set; }
}
