using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Factory for creating backend provider instances by type.
/// Allows ServerManager to resolve the correct IBackendProvider without knowing concrete implementations.
/// </summary>
public interface IBackendProviderFactory
{
    /// <summary>
    /// Registers a factory function for the given server engine.
    /// </summary>
    void Register(ServerEngine engine, Func<IBackendProvider> factory);

    /// <summary>
    /// Creates a new backend provider for the specified server engine.
    /// Returns null if no provider is registered for that engine.
    /// </summary>
    IBackendProvider? Create(ServerEngine engine);
}
