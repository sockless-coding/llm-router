using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Manages the lifecycle of inference server instances.
/// </summary>
public interface IServerManager
{
    /// <summary>
    /// Creates a new server instance with engine-specific configuration and returns it.
    /// </summary>
    Task<ServerInstance> CreateInstanceAsync(string name, ServerEngine engine, BackendConfigData configData, int? port = null);

    /// <summary>
    /// Starts the server using its active preset (or without one if none is set).
    /// </summary>
    Task<bool> StartAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops a running server gracefully.
    /// </summary>
    Task StopAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the current preset and restarts with the specified one.
    /// </summary>
    Task<bool> RestartWithPresetAsync(Guid instanceId, Guid presetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the given preset as active and starts the server if it's idle.
    /// Unlike RestartWithPresetAsync, this does NOT stop a running server first —
    /// it only works on Idle/Errored servers (activates the preset then boots up).
    /// </summary>
    Task<bool> StartWithPresetAsync(Guid instanceId, Guid presetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Safely attempts to auto-start an idle server that has a valid active preset configured.
    /// Returns true if the server is already running or was successfully started.
    /// Returns false if the server cannot be started (no preset, starting/stopping state, etc.).
    /// </summary>
    Task<bool> TryAutoStartAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the health status of a specific server instance.
    /// </summary>
    Task<ServerInstance?> GetHealthAsync(Guid instanceId);

    /// <summary>
    /// Gets all managed server instances (sync).
    /// </summary>
    IReadOnlyList<ServerInstance> GetAllInstances();

    /// <summary>
    /// Gets all managed server instances (async — preferred for SQLite to avoid deadlocks).
    /// </summary>
    Task<IReadOnlyList<ServerInstance>> GetAllInstancesAsync();

    /// <summary>
    /// Updates the engine-specific backend configuration for a server instance.
    /// </summary>
    Task<BackendConfig> UpdateBackendConfigAsync(Guid instanceId, BackendConfigData configData);

    /// <summary>
    /// Removes a server instance from management (stops it first if running).
    /// </summary>
    Task RemoveInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the backend provider for a specific server instance.
    /// Returns null if no provider is registered for this instance.
    /// </summary>
    /// <summary>
    /// Returns the full start command string for a server instance (executable + arguments),
    /// without actually launching the process. Uses the active preset if set.
    /// Returns null if no provider is available or no valid preset is configured.
    /// </summary>
    Task<string?> GetStartCommandAsync(Guid instanceId);

    IBackendProvider? GetProvider(Guid instanceId);

    /// <summary>
    /// Updates the health status of a server instance (used by the health monitor).
    /// </summary>
    Task UpdateHealthAsync(Guid instanceId, bool isHealthy);
}
