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
    private readonly ProviderRegistry _registry;

    public ServerManager(LRDbContext context, IBackendProviderFactory providerFactory, ProviderRegistry registry)
    {
        _context = context;
        _providerFactory = providerFactory;
        _registry = registry;
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
                EnvironmentSetupCommand = configData.EnvironmentSetupCommand,
            };
        }

        _context.ServerInstances.Add(instance);
        await _context.SaveChangesAsync();

        // Create the backend provider for this instance (runtime-only, not persisted)
        var provider = _providerFactory.Create(engine);
        if (provider is not null)
        {
            // Configure the provider with backend-specific settings
            provider.Configure(configData);

            _registry.Register(instance.Id, provider);
        }

        return instance;
    }

    public async Task<bool> StartAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await _context.ServerInstances
            .Include(s => s.Config)
            .FirstOrDefaultAsync(s => s.Id == instanceId)
            ?? throw new KeyNotFoundException($"Server instance {instanceId} not found.");

        var provider = GetOrCreateProvider(instance);

        if (instance.Status == ServerStatus.Running)
            return true;

        // Re-configure the provider with the latest config from the database
        // in case it was updated while the server was stopped
        if (instance.Config is not null)
        {
            var configData = new BackendConfigData
            {
                LlamaCppExecutableFolderPath = instance.Config.LlamaCppExecutableFolderPath,
                CompanionAppPath = instance.Config.CompanionAppPath,
                EnvironmentSetupCommand = instance.Config.EnvironmentSetupCommand,
            };
            provider.Configure(configData);
        }

        ModelPreset? preset = null;
        if (instance.ActivePresetId.HasValue)
        {
            preset = await _context.ModelPresets.FindAsync(instance.ActivePresetId.Value);
        }

        // Validate that we have a valid preset with a model path before starting
        if (preset is null || string.IsNullOrWhiteSpace(preset.ModelPath))
        {
            instance.Status = ServerStatus.Error;
            instance.IsHealthy = false;
            await _context.SaveChangesAsync();
            throw new InvalidOperationException(
                "Cannot start server without a valid model path. Please set an active preset with a ModelPath first.");
        }

        instance.Status = ServerStatus.Starting;

        bool started = await provider.StartProcessAsync(preset, instance.Port, cancellationToken);

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
        var provider = GetOrCreateProvider(instance);
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

    public async Task<bool> StartWithPresetAsync(Guid instanceId, Guid presetId, CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceOrThrow(instanceId);
        if (instance.Status != ServerStatus.Idle && instance.Status != ServerStatus.Error)
            throw new InvalidOperationException($"Cannot start server '{instance.Name}' — current status is {instance.Status}. Stop it first.");

        var preset = await _context.ModelPresets.FindAsync(presetId)
            ?? throw new ArgumentException("Preset not found.", nameof(presetId));

        instance.ActivePresetId = presetId;
        await _context.SaveChangesAsync();

        return await StartAsync(instanceId, cancellationToken);
    }

    public async Task<bool> TryAutoStartAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceOrThrow(instanceId);

        // Already running — nothing to do
        if (instance.Status == ServerStatus.Running)
            return true;

        // Don't interrupt in-flight operations
        if (instance.Status == ServerStatus.Starting || instance.Status == ServerStatus.Stopping)
            return false;

        // Must have a valid active preset to auto-start
        if (!instance.ActivePresetId.HasValue)
            return false;

        var preset = await _context.ModelPresets.FindAsync(instance.ActivePresetId.Value);
        if (preset is null || string.IsNullOrWhiteSpace(preset.ModelPath))
            return false;

        try
        {
            return await StartAsync(instanceId, cancellationToken);
        }
        catch
        {
            return false;
        }
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
        config.EnvironmentSetupCommand = configData.EnvironmentSetupCommand;

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

        _registry.Remove(instanceId);
    }

    public async Task<string?> GetStartCommandAsync(Guid instanceId)
    {
        var instance = await _context.ServerInstances
            .Include(s => s.Config)
            .FirstOrDefaultAsync(s => s.Id == instanceId);

        if (instance is null) return null;

        // Re-configure provider with latest config
        var provider = GetOrCreateProvider(instance);
        if (instance.Config is not null)
        {
            var configData = new BackendConfigData
            {
                LlamaCppExecutableFolderPath = instance.Config.LlamaCppExecutableFolderPath,
                CompanionAppPath = instance.Config.CompanionAppPath,
                EnvironmentSetupCommand = instance.Config.EnvironmentSetupCommand,
            };
            provider.Configure(configData);
        }

        // Get active preset
        ModelPreset? preset = null;
        if (instance.ActivePresetId.HasValue)
        {
            preset = await _context.ModelPresets.FindAsync(instance.ActivePresetId.Value);
        }

        if (preset is null || string.IsNullOrWhiteSpace(preset.ModelPath))
            return null;

        return provider.GetStartCommand(preset, instance.Port);
    }

    public IBackendProvider? GetProvider(Guid instanceId)
    {
        return _registry.TryGet(instanceId, out var provider) ? provider : null;
    }

    public async Task UpdateHealthAsync(Guid instanceId, bool isHealthy)
    {
        var instance = await _context.ServerInstances.FindAsync(instanceId);
        if (instance is not null)
        {
            instance.IsHealthy = isHealthy;
            await _context.SaveChangesAsync();
        }
    }

    private async Task<ServerInstance> GetInstanceOrThrow(Guid instanceId)
    {
        var instance = await _context.ServerInstances.FindAsync(instanceId)
            ?? throw new KeyNotFoundException($"Server instance {instanceId} not found.");
        return instance;
    }

    private IBackendProvider GetOrCreateProvider(ServerInstance instance)
    {
        // Check registry first (fast path — already registered from CreateInstanceAsync or previous call)
        if (_registry.TryGet(instance.Id, out var provider))
            return provider;

        // Lazy registration: create and register a provider for the instance's engine.
        // This handles cases where the app was restarted and existing DB instances
        // don't have in-memory providers yet.
        provider = _providerFactory.Create(instance.Engine);
        if (provider is null)
            throw new InvalidOperationException($"No backend provider available for engine {instance.Engine} on instance {instance.Id}.");

        _registry.Register(instance.Id, provider);
        return provider;
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
