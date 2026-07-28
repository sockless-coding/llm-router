using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Factory that creates the correct IBackendProvider based on ServerEngine.
/// Uses mock implementations by default; swap in real providers via DI overrides.
/// </summary>
public class BackendProviderFactory : IBackendProviderFactory
{
    private readonly Dictionary<ServerEngine, Func<IBackendProvider>> _factories = new();

    public BackendProviderFactory()
    {
        // Register default mock provider for llama.cpp
        Register(ServerEngine.LlamaCpp, () => new MockLlamaCppProvider());
    }

    /// <summary>
    /// Registers a factory function for the given server engine.
    /// Use this to override mock providers with real implementations at runtime.
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
