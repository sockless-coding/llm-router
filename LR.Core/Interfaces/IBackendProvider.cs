using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Contract that any inference engine (llama.cpp, Ollama, vLLM, etc.) must implement.
/// This is the primary extension point for adding new backends.
/// </summary>
public interface IBackendProvider
{
    /// <summary>
    /// The backend type this provider supports.
    /// </summary>
    BackendType SupportedBackend { get; }

    /// <summary>
    /// Starts the inference server process with the given preset configuration.
    /// Returns true if the server started successfully.
    /// </summary>
    Task<bool> StartProcessAsync(ModelPreset preset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the running inference server process gracefully.
    /// </summary>
    Task StopProcessAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the server is alive and responsive.
    /// Returns true if healthy, false otherwise.
    /// </summary>
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an inference request to the running server and returns the response with performance metrics.
    /// </summary>
    Task<RouteResponse?> SendRequestAsync(string payload, CancellationToken cancellationToken = default);
}
