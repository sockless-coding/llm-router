using Microsoft.Extensions.DependencyInjection;

using LR.Core.Interfaces;
using LR.Core.Services;
using LR.Providers.LlamaCpp;
using LR.Providers.Strata;

namespace LR.Providers;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers every supported server engine and the provider factory that creates them.
    /// To add an engine: add a <see cref="LR.Core.Models.ServerEngine"/> value, implement an
    /// <see cref="IBackendProvider"/> (usually on <see cref="ManagedServerProviderBase"/>) and an
    /// <see cref="IEngineDescriptor"/>, and register the descriptor here.
    /// </summary>
    public static IServiceCollection AddBackendEngines(this IServiceCollection services)
    {
        services.AddSingleton<IEngineDescriptor, LlamaCppEngineDescriptor>();
        services.AddSingleton<IEngineDescriptor, StrataEngineDescriptor>();

        services.AddSingleton<IEngineCatalog, EngineCatalog>();
        services.AddSingleton<IBackendProviderFactory, BackendProviderFactory>();
        return services;
    }
}
