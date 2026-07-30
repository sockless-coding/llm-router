using LR.Core.Interfaces;

namespace LR.Core.Services;

/// <summary>
/// Singleton registry that holds runtime IBackendProvider references for server instances.
/// Shared across all scoped ServerManager instances so providers survive request boundaries.
/// </summary>
public class ProviderRegistry
{
    private readonly Dictionary<Guid, IBackendProvider> _providers = new();

    public void Register(Guid instanceId, IBackendProvider provider)
    {
        _providers[instanceId] = provider;
    }

    public bool TryGet(Guid instanceId, out IBackendProvider? provider)
    {
        return _providers.TryGetValue(instanceId, out provider);
    }

    public void Remove(Guid instanceId)
    {
        _providers.Remove(instanceId);
    }
}
