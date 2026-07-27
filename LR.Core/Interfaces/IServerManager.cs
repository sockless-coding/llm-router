using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Manages the lifecycle of inference server instances.
/// </summary>
public interface IServerManager
{
    /// <summary>
    /// Creates a new server instance and returns it.
    /// </summary>
    Task<ServerInstance> CreateInstanceAsync(string name, BackendType backendType, int? port = null);

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
    /// Gets the health status of a specific server instance.
    /// </summary>
    Task<ServerInstance?> GetHealthAsync(Guid instanceId);

    /// <summary>
    /// Gets all managed server instances.
    /// </summary>
    IReadOnlyList<ServerInstance> GetAllInstances();

    /// <summary>
    /// Removes a server instance from management (stops it first if running).
    /// </summary>
    Task RemoveInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default);
}
