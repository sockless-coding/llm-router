using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Factory that creates the correct IBackendProvider based on ServerEngine.
/// Uses LlamaCppProvider by default; register custom providers via Register().
/// </summary>
public class BackendProviderFactory : IBackendProviderFactory
{
    private readonly Dictionary<ServerEngine, Func<IBackendProvider>> _factories = new();

    public BackendProviderFactory()
    {
        // Register default real provider for llama.cpp
        Register(ServerEngine.LlamaCpp, () => new LlamaCppProvider());
    }

    /// <summary>
    /// Registers a factory function for the given server engine.
    /// Use this to override with custom implementations at runtime.
    /// </summary>
    public void Register(ServerEngine engine, Func<IBackendProvider> factory)
    {
        _factories[engine] = factory;
    }

    public IBackendProvider? Create(ServerEngine engine)
    {
        if (_factories.TryGetValue(engine, out var factory))
            return factory();

        return null;
    }
}
