namespace LR.Core.Models;

/// <summary>
/// Represents a managed inference server instance.
/// </summary>
public class ServerInstance
{
    public Guid Id { get; set; } = Guid.NewGuid();
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
    public string? Url { get; set; }

    /// <summary>
    /// Optional port override for the backend process.
    /// </summary>
    public int? Port { get; set; }
}
