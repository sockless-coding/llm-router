using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Factory for creating backend provider instances by type.
/// Allows ServerManager to resolve the correct IBackendProvider without knowing concrete implementations.
/// </summary>
public interface IBackendProviderFactory
{
    /// <summary>
    /// Creates a new backend provider for the specified backend type.
    /// Returns null if no provider is registered for that type.
    /// </summary>
    IBackendProvider? Create(BackendType backendType);
}
