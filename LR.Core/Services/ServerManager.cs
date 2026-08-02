using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISignalRProgressPublisher _progressPublisher;

    public ServerManager(
        LRDbContext context,
        IBackendProviderFactory providerFactory,
        ProviderRegistry registry,
        IServiceScopeFactory scopeFactory,
        ISignalRProgressPublisher progressPublisher)
    {
        _context = context;
        _providerFactory = providerFactory;
        _registry = registry;
        _scopeFactory = scopeFactory;
        _progressPublisher = progressPublisher;
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

            // Set server instance reference so provider can log to DB and detect crashes
            provider.SetServerInstance(instance);

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

        if (instance.Status == ServerStatus.Running)
            return true;

        var provider = GetOrCreateProvider(instance);

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

        // Set status to Starting and persist immediately — then offload to background task
        instance.Status = ServerStatus.Starting;
        await LogLifecycleEvent(instance, ServerLogLevel.Info,
            $"Starting server '{instance.Name}' on port {instance.Port}...");

        var progressEvent = new StartupProgressEvent
        {
            InstanceId = instance.Id,
            EventType = StartupEventType.Starting,
            Message = $"Server '{instance.Name}' is starting...",
            ElapsedSeconds = 0
        };
        await BroadcastStartupProgress(progressEvent);

        // Offload the actual startup to a background task so the API returns immediately
        var scopeFactory = _scopeFactory;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();

                bool started = await provider.StartProcessAsync(
                    preset!, instance.Port,
                    onProgress: async (e) =>
                    {
                        e.InstanceId = instance.Id; // ensure correct ID
                        if (e.EventType == StartupEventType.Healthy)
                        {
                            var inst = await context.ServerInstances.FindAsync(instance.Id);
                            if (inst != null)
                            {
                                inst.Status = ServerStatus.Running;
                                inst.Url = $"http://localhost:{instance.Port}";
                                inst.IsHealthy = true;
                                await LogLifecycleEvent(inst, ServerLogLevel.Info,
                                    $"Server '{inst.Name}' started successfully on {inst.Url}.");
                            }
                        }
                        else if (e.EventType == StartupEventType.Error)
                        {
                            var inst = await context.ServerInstances.FindAsync(instance.Id);
                            if (inst != null)
                            {
                                inst.Status = ServerStatus.Error;
                                inst.IsHealthy = false;
                                await LogLifecycleEvent(inst, ServerLogLevel.Error,
                                    $"Server '{inst.Name}' failed to start: {e.Message}");
                            }
                        }
                        await context.SaveChangesAsync();
                        await BroadcastStartupProgress(e);
                    },
                    cancellationToken);

                if (!started)
                {
                    var inst = await context.ServerInstances.FindAsync(instance.Id);
                    if (inst != null)
                    {
                        inst.Status = ServerStatus.Error;
                        inst.IsHealthy = false;
                        await LogLifecycleEvent(inst, ServerLogLevel.Error,
                            $"Server '{inst.Name}' failed to start.");
                    }
                    await context.SaveChangesAsync();

                    await BroadcastStartupProgress(new StartupProgressEvent
                    {
                        InstanceId = instance.Id,
                        EventType = StartupEventType.Error,
                        Message = $"Server '{instance.Name}' failed to start."
                    });
                }
            }
            catch (Exception ex)
            {
                using var scope2 = scopeFactory.CreateScope();
                var context2 = scope2.ServiceProvider.GetRequiredService<LRDbContext>();

                var inst = await context2.ServerInstances.FindAsync(instance.Id);
                if (inst != null)
                {
                    inst.Status = ServerStatus.Error;
                    inst.IsHealthy = false;
                    await LogLifecycleEvent(inst, ServerLogLevel.Error,
                        $"Server '{inst.Name}' crashed during startup: {ex.Message}");
                }
                await context2.SaveChangesAsync();

                await BroadcastStartupProgress(new StartupProgressEvent
                {
                    InstanceId = instance.Id,
                    EventType = StartupEventType.Error,
                    Message = ex.Message
                });
            }
        }, cancellationToken);

        // Return immediately — startup is in progress
        return true;
    }

    public async Task StopAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceOrThrow(instanceId);
        if (instance.Status != ServerStatus.Running)
            return;

        instance.Status = ServerStatus.Stopping;
        await LogLifecycleEvent(instance, ServerLogLevel.Info,
            $"Stopping server '{instance.Name}'...");

        var provider = GetOrCreateProvider(instance);
        try
        {
            await provider.StopProcessAsync(cancellationToken);
            instance.Status = ServerStatus.Idle;
            await LogLifecycleEvent(instance, ServerLogLevel.Info,
                $"Server '{instance.Name}' stopped successfully.");
        }
        catch (Exception ex)
        {
            instance.Status = ServerStatus.Error;
            await LogLifecycleEvent(instance, ServerLogLevel.Error,
                $"Error stopping server '{instance.Name}': {ex.Message}");
            throw;
        }

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

    /// <summary>
    /// Broadcasts a startup progress event to all connected SignalR clients.
    /// </summary>
    private async Task BroadcastStartupProgress(StartupProgressEvent @event)
    {
        try
        {
            await _progressPublisher.PublishAsync(@event);
        }
        catch
        {
            // Ignore SignalR broadcast failures — don't let them break startup
        }
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

        // Set server instance reference so provider can log to DB and detect crashes
        provider.SetServerInstance(instance);

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

    /// <summary>
    /// Logs a lifecycle event to the database via scoped resolution of IServerLogService.
    /// This ensures start/stop/crash events are always persisted regardless of provider state.
    /// </summary>
    private async Task LogLifecycleEvent(ServerInstance instance, ServerLogLevel level, string message)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var logService = scope.ServiceProvider.GetRequiredService<IServerLogService>();
            await logService.LogAsync(instance, level, message);
        }
        catch (Exception ex)
        {
            // Don't let logging failures break server lifecycle operations
            System.Diagnostics.Debug.WriteLine($"Failed to persist lifecycle event: {ex.Message}");
        }
    }
}
