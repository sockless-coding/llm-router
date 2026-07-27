using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Manages the lifecycle of inference server instances.
/// Coordinates with IBackendProvider for process control and IPresetManager for model switching.
/// </summary>
public class ServerManager : IServerManager
{
    private readonly IBackendProviderFactory _providerFactory;
    private readonly IPresetManager _presetManager;
    private readonly Dictionary<Guid, ServerInstance> _instances = new();
    private readonly Dictionary<Guid, IBackendProvider> _providers = new();

    public ServerManager(IBackendProviderFactory providerFactory, IPresetManager presetManager)
    {
        _providerFactory = providerFactory;
        _presetManager = presetManager;
    }

    public async Task<ServerInstance> CreateInstanceAsync(string name, BackendType backendType, int? port = null)
    {
        var instance = new ServerInstance
        {
            Name = name,
            BackendType = backendType,
            Status = ServerStatus.Idle,
            Port = port ?? GetNextAvailablePort(),
        };

        _instances[instance.Id] = instance;

        // Create the backend provider for this instance
        var provider = _providerFactory.Create(backendType);
        if (provider is not null)
            _providers[instance.Id] = provider;

        return await Task.FromResult(instance);
    }

    public async Task<bool> StartAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = GetInstanceOrThrow(instanceId);
        var provider = GetProviderOrThrow(instanceId);

        if (instance.Status == ServerStatus.Running)
            return true;

        instance.Status = ServerStatus.Stopping; // Transition state during start

        ModelPreset? preset = null;
        if (instance.ActivePresetId.HasValue)
        {
            preset = _presetManager.GetById(instance.ActivePresetId.Value);
        }

        bool started;
        if (preset is not null)
            started = await provider.StartProcessAsync(preset, cancellationToken);
        else
            started = await provider.StartProcessAsync(CreateDefaultPreset(instance), cancellationToken);

        if (started)
        {
            instance.Status = ServerStatus.Running;
            instance.Url = $"http://localhost:{instance.Port}";
            instance.IsHealthy = true;
        }
        else
        {
            instance.Status = ServerStatus.Error;
            instance.IsHealthy = false;
        }

        return await Task.FromResult(started);
    }

    public async Task StopAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = GetInstanceOrThrow(instanceId);
        if (instance.Status != ServerStatus.Running)
            return;

        instance.Status = ServerStatus.Stopping;
        var provider = GetProviderOrThrow(instanceId);
        await provider.StopProcessAsync(cancellationToken);

        instance.Status = ServerStatus.Idle;
    }

    public async Task<bool> RestartWithPresetAsync(Guid instanceId, Guid presetId, CancellationToken cancellationToken = default)
    {
        var instance = GetInstanceOrThrow(instanceId);
        var preset = _presetManager.GetById(presetId) ?? throw new ArgumentException("Preset not found.", nameof(presetId));

        if (instance.Status == ServerStatus.Running)
            await StopAsync(instanceId, cancellationToken);

        instance.ActivePresetId = presetId;
        return await StartAsync(instanceId, cancellationToken);
    }

    public Task<ServerInstance?> GetHealthAsync(Guid instanceId)
    {
        if (!_instances.TryGetValue(instanceId, out var instance))
            return Task.FromResult<ServerInstance?>(null);

        // Deep clone to avoid mutation issues
        var snapshot = new ServerInstance
        {
            Id = instance.Id,
            Name = instance.Name,
            BackendType = instance.BackendType,
            Status = instance.Status,
            IsHealthy = instance.IsHealthy,
            ActivePresetId = instance.ActivePresetId,
            Url = instance.Url,
            Port = instance.Port,
        };

        return Task.FromResult<ServerInstance?>(snapshot);
    }

    public IReadOnlyList<ServerInstance> GetAllInstances()
    {
        return _instances.Values.Select(i => new ServerInstance
        {
            Id = i.Id,
            Name = i.Name,
            BackendType = i.BackendType,
            Status = i.Status,
            IsHealthy = i.IsHealthy,
            ActivePresetId = i.ActivePresetId,
            Url = i.Url,
            Port = i.Port,
        }).ToList().AsReadOnly();
    }

    public async Task RemoveInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        if (_instances.TryGetValue(instanceId, out var instance) && instance.Status == ServerStatus.Running)
            await StopAsync(instanceId, cancellationToken);

        _instances.Remove(instanceId);
        _providers.Remove(instanceId);
    }

    private ServerInstance GetInstanceOrThrow(Guid instanceId)
    {
        if (!_instances.TryGetValue(instanceId, out var instance))
            throw new KeyNotFoundException($"Server instance {instanceId} not found.");
        return instance;
    }

    private IBackendProvider GetProviderOrThrow(Guid instanceId)
    {
        if (!_providers.TryGetValue(instanceId, out var provider))
            throw new InvalidOperationException($"No backend provider registered for instance {instanceId}.");
        return provider;
    }

    /// <summary>
    /// Creates a minimal preset when no active preset is set (used by StartAsync as fallback).
    /// </summary>
    private ModelPreset CreateDefaultPreset(ServerInstance instance)
    {
        return new ModelPreset
        {
            Id = Guid.NewGuid(),
            ServerInstanceId = instance.Id,
            Name = "default",
            ModelPath = string.Empty,
            ContextLength = 4096,
            GpuLayers = -1,
        };
    }

    /// <summary>
    /// Simple port increment strategy. In production, this should check actual port availability.
    /// </summary>
    private int GetNextAvailablePort()
    {
        var basePort = 8080;
        var usedPorts = _instances.Values.Select(i => i.Port).ToList();
        for (int p = basePort; ; p++)
        {
            if (!usedPorts.Contains(p))
                return p;
        }
    }
}
