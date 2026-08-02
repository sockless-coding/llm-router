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

    /// <summary>
    /// The server engine (e.g., llama.cpp, Ollama) this instance runs.
    /// </summary>
    public ServerEngine Engine { get; set; }
    public ServerStatus Status { get; set; }
    public bool IsHealthy { get; set; }

    /// <summary>
    /// Whether this server is currently processing a request (runtime-only, not persisted).
    /// </summary>
    [NotMapped]
    public bool IsBusy { get; set; }

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
    /// The last error message recorded for this server (e.g., crash reason, health check failure).
    /// </summary>
    [MaxLength(2048)]
    public string? LastErrorMessage { get; set; }

    /// <summary>
    /// When the last error occurred on this server.
    /// </summary>
    public DateTime? LastErrorTime { get; set; }

    /// <summary>
    /// How many times this server has been auto-restarted since its last manual start.
    /// </summary>
    public int RestartCount { get; set; }

    /// <summary>
    /// Maximum number of auto-restart attempts before giving up and marking the server as Error.
    /// </summary>
    public int MaxRestarts { get; set; } = 3;

    /// <summary>
    /// Whether an auto-restart is currently in progress (prevents concurrent restart attempts).
    /// </summary>
    [NotMapped]
    public bool IsAutoRestarting { get; set; }

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

    /// <summary>
    /// Navigation: engine-specific configuration for this server instance.
    /// </summary>
    public BackendConfig? Config { get; set; }

    /// <summary>
    /// Navigation: log entries for this server instance.
    /// </summary>
    [ForeignKey(nameof(ServerLog.ServerInstanceId))]
    public ICollection<ServerLog> Logs { get; set; } = new List<ServerLog>();
}
