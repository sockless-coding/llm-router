namespace LR.Core.Models;

/// <summary>
/// Configuration for the API gateway that exposes protocol-compatible endpoints.
/// </summary>
public class GatewaySettings
{
    /// <summary>
    /// The port the gateway listens on. Default: 8080.
    /// </summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// Which protocol endpoints to enable. If empty, all protocols are enabled.
    /// Values: "OpenAI", "Claude", "Ollama" (case-insensitive).
    /// </summary>
    public ApiProtocol[] EnabledProtocols { get; set; } = Array.Empty<ApiProtocol>();

    /// <summary>
    /// Maximum number of requests that can be queued when all servers are busy.
    /// Default: 100. Set to -1 for unlimited.
    /// </summary>
    public int MaxQueueSize { get; set; } = 100;

    /// <summary>
    /// How long a request can wait in the queue before timing out (in seconds).
    /// Default: 300 (5 minutes). Set to -1 for no timeout.
    /// </summary>
    public int QueueTimeoutSeconds { get; set; } = 300;

    // ── Request logging settings ─────────────────────────────────────

    /// <summary>
    /// Whether to log API requests. When disabled, logging is a no-op (zero overhead).
    /// Default: true.
    /// </summary>
    public bool EnableRequestLogging { get; set; } = true;

    /// <summary>
    /// Whether to store full payloads in the database.
    /// When false, backend responses and outgoing payloads are summarized/truncated for privacy and storage efficiency.
    /// Default: false.
    /// </summary>
    public bool LogFullPayloads { get; set; } = false;

    /// <summary>
    /// How many days to keep request log entries. Logs older than this will be automatically purged.
    /// Default: 7.
    /// </summary>
    public int RequestLogRetentionDays { get; set; } = 7;

    // ── Backend timeout settings ─────────────────────────────────────

    /// <summary>
    /// Timeout for backend (inference server) requests in seconds.
    /// This is independent of the client connection — if the client disconnects,
    /// the backend call continues until this timeout or completion.
    /// Default: 300 (5 minutes). Set to -1 for no timeout.
    /// </summary>
    public int BackendTimeoutSeconds { get; set; } = 300;
}
