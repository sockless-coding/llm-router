using Microsoft.Extensions.DependencyInjection;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Factory that creates the correct IBackendProvider based on ServerEngine.
/// Each engine registered in the <see cref="IEngineCatalog"/> gets its descriptor's
/// <see cref="IEngineDescriptor.ProviderType"/>, created through DI; register custom
/// providers via Register() to override.
/// </summary>
public class BackendProviderFactory : IBackendProviderFactory
{
    private readonly Dictionary<ServerEngine, Func<IServiceProvider, IBackendProvider>> _factories = new();
    private readonly IServiceProvider _serviceProvider;

    public BackendProviderFactory(IServiceProvider serviceProvider, IEngineCatalog engines)
    {
        _serviceProvider = serviceProvider;

        foreach (var engine in engines.All)
        {
            var providerType = engine.ProviderType;
            _factories[engine.Engine] = sp => (IBackendProvider)ActivatorUtilities.CreateInstance(sp, providerType);
        }
    }

    /// <summary>
    /// Registers a factory function for the given server engine.
    /// Use this to override with custom implementations at runtime.
    /// </summary>
    public void Register(ServerEngine engine, Func<IBackendProvider> factory)
    {
        // Wrap the old-style factory to accept IServiceProvider (ignoring it)
        _factories[engine] = sp => factory();
    }

    public IBackendProvider? Create(ServerEngine engine)
    {
        if (_factories.TryGetValue(engine, out var factory))
            return factory(_serviceProvider);

        return null;
    }
}
