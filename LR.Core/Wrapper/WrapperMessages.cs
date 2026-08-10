using System.Text.Json.Serialization;

namespace LR.Core.Wrapper;

/// <summary>
/// Which stream a captured output line came from.
/// </summary>
public enum WrapperOutputStream
{
    Stdout,
    Stderr
}

/// <summary>
/// Base type for all messages exchanged over the router&lt;-&gt;wrapper named pipe.
/// Serialized as NDJSON with a "$type" discriminator so both ends can share one wire format.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(StartServerCommand), "start")]
[JsonDerivedType(typeof(StopCommand), "stop")]
[JsonDerivedType(typeof(PingCommand), "ping")]
[JsonDerivedType(typeof(HelloEvent), "hello")]
[JsonDerivedType(typeof(OutputLineEvent), "output")]
[JsonDerivedType(typeof(ProcessStartedEvent), "started")]
[JsonDerivedType(typeof(ProcessExitedEvent), "exited")]
[JsonDerivedType(typeof(CommandAckEvent), "ack")]
public abstract class WrapperMessage
{
}

// --- Router -> Wrapper commands ---

/// <summary>
/// Idempotent "ensure running" command: starts the companion app if not already running
/// (or if its configured path changed), stops any currently-running main server, then
/// starts a new main server with the given args. Used for fresh start, crash auto-restart,
/// and preset-restart alike — the companion app is never disturbed by this command.
/// </summary>
public sealed class StartServerCommand : WrapperMessage
{
    public string ExecutablePath { get; set; } = string.Empty;
    public List<string> Arguments { get; set; } = new();
    public string? WorkingDirectory { get; set; }
    public string? EnvironmentSetupCommand { get; set; }
    public string? CompanionAppPath { get; set; }

    /// <summary>The port the server is expected to listen on, reported back via <see cref="HelloEvent"/>.</summary>
    public int? Port { get; set; }
}

/// <summary>
/// Stops the main server process, optionally the companion app, and optionally tells the
/// wrapper to exit afterward (a full user-initiated Stop).
/// </summary>
public sealed class StopCommand : WrapperMessage
{
    public bool StopCompanion { get; set; }
    public bool ShutdownWrapper { get; set; }
}

/// <summary>
/// Liveness/handshake check — the wrapper responds with a fresh <see cref="HelloEvent"/>.
/// </summary>
public sealed class PingCommand : WrapperMessage
{
}

// --- Wrapper -> Router events ---

/// <summary>
/// Sent immediately on every connection (fresh start and reconnect alike) so the router
/// always knows current reality without extra round trips.
/// </summary>
public sealed class HelloEvent : WrapperMessage
{
    public int WrapperPid { get; set; }
    public int? ServerPid { get; set; }
    public bool ServerRunning { get; set; }
    public bool CompanionRunning { get; set; }
    public int? Port { get; set; }
    public List<string> RecentOutputBacklog { get; set; } = new();
}

/// <summary>
/// A single line of captured stdout/stderr from the managed server process.
/// </summary>
public sealed class OutputLineEvent : WrapperMessage
{
    public WrapperOutputStream Stream { get; set; }
    public string Line { get; set; } = string.Empty;
}

/// <summary>
/// The main server process has just been launched (model loading in progress).
/// </summary>
public sealed class ProcessStartedEvent : WrapperMessage
{
    public int ServerPid { get; set; }
}

/// <summary>
/// The main server process has exited, whether cleanly, via crash, or via a Stop command.
/// </summary>
public sealed class ProcessExitedEvent : WrapperMessage
{
    public int ExitCode { get; set; }
}

/// <summary>
/// Acknowledges a <see cref="StartServerCommand"/> or <see cref="StopCommand"/>.
/// </summary>
public sealed class CommandAckEvent : WrapperMessage
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}
