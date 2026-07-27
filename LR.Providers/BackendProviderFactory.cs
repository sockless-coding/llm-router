using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Providers;

/// <summary>
/// Factory that creates the correct IBackendProvider based on BackendType.
/// Uses mock implementations by default; swap in real providers via DI overrides.
/// </summary>
public class BackendProviderFactory : IBackendProviderFactory
{
    private readonly Dictionary<BackendType, Func<IBackendProvider>> _factories = new();

    public BackendProviderFactory()
    {
        // Register default mock providers
        Register(BackendType.Cuda, () => new MockLlamaCppProvider());
        Register(BackendType.Vulkan, () => new MockVulkanProvider());
        Register(BackendType.Sycl, () => new MockSyclProvider());
        Register(BackendType.Cpu, () => new MockCpuProvider());
    }

    /// <summary>
    /// Registers a factory function for the given backend type.
    /// Use this to override mock providers with real implementations at runtime.
    /// </summary>
    public void Register(BackendType backendType, Func<IBackendProvider> factory)
    {
        _factories[backendType] = factory;
    }

    public IBackendProvider? Create(BackendType backendType)
    {
        if (_factories.TryGetValue(backendType, out var factory))
            return factory();

        return null;
    }
}
