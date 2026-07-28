using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Manages the lifecycle of inference server instances.
/// Coordinates with IBackendProvider for process control and IPresetManager for model switching.
/// Uses EF Core for persistence; runtime provider references are kept in-memory.
/// </summary>
public class ServerManager : IServerManager
{
    private readonly LRDbContext _context;
    private readonly IBackendProviderFactory _providerFactory;
    private readonly Dictionary<Guid, IBackendProvider> _providers = new();

    public ServerManager(LRDbContext context, IBackendProviderFactory providerFactory)
    {
        _context = context;
        _providerFactory = providerFactory;
    }

    public async Task<ServerInstance> CreateInstanceAsync(string name, ServerEngine engine, BackendConfigData configData, int? port = null)
    {
        var instance = new ServerInstance
        {
            Name = name,
            Engine = engine,
            Status = ServerStatus.Idle,
            Port = port ?? await GetNextAvailablePort(),
        };

        // Create the backend config entity alongside the server instance
        if (engine == ServerEngine.LlamaCpp)
        {
            instance.Config = new BackendConfig
            {
                LlamaCppExecutableFolderPath = configData.LlamaCppExecutableFolderPath,
                CompanionAppPath = configData.CompanionAppPath,
            };
        }

        _context.ServerInstances.Add(instance);
        await _context.SaveChangesAsync();

        // Create the backend provider for this instance (runtime-only, not persisted)
        var provider = _providerFactory.Create(engine);
        if (provider is not null)
            _providers[instance.Id] = provider;

        return instance;
    }

    public async Task<bool> StartAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceOrThrow(instanceId);
        var provider = GetProviderOrThrow(instanceId);

        if (instance.Status == ServerStatus.Running)
            return true;

        instance.Status = ServerStatus.Stopping; // Transition state during start

        ModelPreset? preset = null;
        if (instance.ActivePresetId.HasValue)
        {
            preset = await _context.ModelPresets.FindAsync(instance.ActivePresetId.Value);
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

        await _context.SaveChangesAsync();
        return started;
    }

    public async Task StopAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceOrThrow(instanceId);
        if (instance.Status != ServerStatus.Running)
            return;

        instance.Status = ServerStatus.Stopping;
        var provider = GetProviderOrThrow(instanceId);
        await provider.StopProcessAsync(cancellationToken);

        instance.Status = ServerStatus.Idle;
        await _context.SaveChangesAsync();
    }

    public async Task<bool> RestartWithPresetAsync(Guid instanceId, Guid presetId, CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceOrThrow(instanceId);
        var preset = await _context.ModelPresets.FindAsync(presetId) ?? throw new ArgumentException("Preset not found.", nameof(presetId));

        if (instance.Status == ServerStatus.Running)
            await StopAsync(instanceId, cancellationToken);

        instance.ActivePresetId = presetId;
        return await StartAsync(instanceId, cancellationToken);
    }

    public async Task<ServerInstance?> GetHealthAsync(Guid instanceId)
    {
        var instance = await _context.ServerInstances.FindAsync(instanceId);
        if (instance is null) return null;

        // Return a snapshot to avoid mutation issues
        return new ServerInstance
        {
            Id = instance.Id,
            Name = instance.Name,
            Engine = instance.Engine,
            Status = instance.Status,
            IsHealthy = instance.IsHealthy,
            ActivePresetId = instance.ActivePresetId,
            Url = instance.Url,
            Port = instance.Port,
        };
    }

    public async Task<IReadOnlyList<ServerInstance>> GetAllInstancesAsync()
    {
        var instances = await _context.ServerInstances.ToListAsync();
        return instances.Select(i => new ServerInstance
        {
            Id = i.Id,
            Name = i.Name,
            Engine = i.Engine,
            Status = i.Status,
            IsHealthy = i.IsHealthy,
            ActivePresetId = i.ActivePresetId,
            Url = i.Url,
            Port = i.Port,
        }).ToList().AsReadOnly();
    }

    public IReadOnlyList<ServerInstance> GetAllInstances()
    {
        return _context.ServerInstances.ToList().Select(i => new ServerInstance
        {
            Id = i.Id,
            Name = i.Name,
            Engine = i.Engine,
            Status = i.Status,
            IsHealthy = i.IsHealthy,
            ActivePresetId = i.ActivePresetId,
            Url = i.Url,
            Port = i.Port,
        }).ToList().AsReadOnly();
    }

    public async Task<BackendConfig> UpdateBackendConfigAsync(Guid instanceId, BackendConfigData configData)
    {
        var config = await _context.BackendConfigs.FirstOrDefaultAsync(c => c.ServerInstanceId == instanceId)
            ?? throw new KeyNotFoundException($"Backend config for server {instanceId} not found.");

        config.LlamaCppExecutableFolderPath = configData.LlamaCppExecutableFolderPath;
        config.CompanionAppPath = configData.CompanionAppPath;

        await _context.SaveChangesAsync();
        return config;
    }

    public async Task RemoveInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await _context.ServerInstances.FindAsync(instanceId);
        if (instance is not null && instance.Status == ServerStatus.Running)
            await StopAsync(instanceId, cancellationToken);

        if (instance is not null)
        {
            _context.ServerInstances.Remove(instance);
            await _context.SaveChangesAsync();
        }

        _providers.Remove(instanceId);
    }

    private async Task<ServerInstance> GetInstanceOrThrow(Guid instanceId)
    {
        var instance = await _context.ServerInstances.FindAsync(instanceId)
            ?? throw new KeyNotFoundException($"Server instance {instanceId} not found.");
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
            ContextSize = 4096,
            GpuLayers = -1,
        };
    }

    /// <summary>
    /// Simple port increment strategy. In production, this should check actual port availability.
    /// </summary>
    private async Task<int> GetNextAvailablePort()
    {
        var basePort = 8080;
        var usedPorts = await _context.ServerInstances.Select(i => i.Port).ToListAsync();
        for (int p = basePort; ; p++)
        {
            if (!usedPorts.Contains(p))
                return p;
        }
    }
}
