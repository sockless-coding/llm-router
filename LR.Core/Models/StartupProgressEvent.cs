namespace LR.Core.Models;

/// <summary>
/// Represents a progress event during server startup, broadcast via SignalR.
/// </summary>
public class StartupProgressEvent
{
    /// <summary>The server instance this event belongs to.</summary>
    public Guid InstanceId { get; set; }

    /// <summary>The type of progress event.</summary>
    public StartupEventType EventType { get; set; }

    /// <summary>Human-readable message describing the current state.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Time elapsed since startup began (in seconds).</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>Timestamp of this event.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
