using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Contract that any inference engine (llama.cpp, Ollama, vLLM, etc.) must implement.
/// This is the primary extension point for adding new backends.
/// </summary>
public interface IBackendProvider
{
    /// <summary>
    /// The server engine this provider supports (e.g., llama.cpp, Ollama).
    /// </summary>
    ServerEngine Engine { get; }

    /// <summary>
    /// Starts the inference server process with the given preset configuration.
    /// The optional port parameter overrides the provider's configured port for this start operation.
    /// The onProgress callback is invoked at key startup milestones (process started, health check polling, healthy).
    /// Returns true if the server started successfully.
    /// </summary>
    Task<bool> StartProcessAsync(ModelPreset preset, int? port = null, Func<StartupProgressEvent, Task>? onProgress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the running inference server process gracefully.
    /// </summary>
    Task StopProcessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Restarts the server process with a new preset without disturbing any companion/support
    /// processes the provider may be managing alongside it (e.g. a GPU companion app). Falls
    /// back to full start semantics if nothing is currently running. Same progress/timeout
    /// contract as <see cref="StartProcessAsync"/>.
    /// </summary>
    Task<bool> RestartProcessAsync(ModelPreset preset, int? port = null, Func<StartupProgressEvent, Task>? onProgress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to re-attach to a process this provider previously started, which may still be
    /// running after the router itself restarted. Returns true if a live, running server was
    /// found and reattached (with output/health monitoring resumed); false if there was nothing
    /// to reattach to. Providers with no such out-of-process persistence mechanism return false.
    /// </summary>
    Task<bool> TryReconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the server is alive and responsive.
    /// Returns true if healthy, false otherwise.
    /// </summary>
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an inference request to the running server and returns the response with performance metrics.
    /// The protocol indicates the wire shape of <paramref name="payload"/> — providers that don't
    /// natively speak a protocol are expected to translate; providers that do (e.g. llama.cpp's
    /// native /v1/messages support for Claude) can route to the matching backend endpoint directly.
    /// </summary>
    Task<RouteResponse?> SendRequestAsync(string payload, ApiProtocol protocol = ApiProtocol.OpenAI, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a streaming inference request. Returns token chunks as they are generated.
    /// Each yielded string is a single text delta/token from the model.
    /// After all tokens are yielded, the final yield contains the RouteResponse metadata.
    /// </summary>
    IAsyncEnumerable<RouteStreamChunk> SendStreamRequestAsync(string payload, ApiProtocol protocol = ApiProtocol.OpenAI, CancellationToken cancellationToken = default);

    /// <summary>
    /// Configures the provider with engine-specific settings (paths, environment setup, etc.).
    /// Called by ServerManager before starting the process. Override to apply configuration.
    /// </summary>
    void Configure(BackendConfigData configData);

    /// <summary>
    /// Sets the server instance reference for logging and crash detection purposes.
    /// Called by ServerManager after creating or lazily registering a provider.
    /// </summary>
    void SetServerInstance(ServerInstance? instance);

    /// <summary>
    /// Returns the full command-line string that would be used to start this server,
    /// without actually launching the process. Useful for debugging and UI display.
    /// Returns null if no executable path is configured or the preset is invalid.
    /// </summary>
    string? GetStartCommand(ModelPreset preset, int? port = null);
}
