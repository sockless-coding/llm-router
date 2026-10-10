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
    private readonly IServerConcurrencyLimiter _limiter;

    public ServerManager(
        LRDbContext context,
        IBackendProviderFactory providerFactory,
        ProviderRegistry registry,
        IServiceScopeFactory scopeFactory,
        ISignalRProgressPublisher progressPublisher,
        IServerConcurrencyLimiter limiter)
    {
        _context = context;
        _providerFactory = providerFactory;
        _registry = registry;
        _scopeFactory = scopeFactory;
        _progressPublisher = progressPublisher;
        _limiter = limiter;
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
        instance.Config = new BackendConfig
        {
            InstallFolderPath = configData.InstallFolderPath,
            CompanionAppPath = configData.CompanionAppPath,
            EnvironmentSetupCommand = configData.EnvironmentSetupCommand,
            EngineBuildId = configData.EngineBuildId,
        };

        _context.ServerInstances.Add(instance);
        await _context.SaveChangesAsync();

        // Create the backend provider for this instance (runtime-only, not persisted)
        var provider = _providerFactory.Create(engine);
        if (provider is not null)
        {
            // Configure the provider with backend-specific settings
            provider.Configure(instance.Config is not null ? await ResolveConfigDataAsync(instance.Config) : configData);

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

        return await StartOrRestartAsync(instance, isRestart: false, cancellationToken);
    }

    /// <summary>
    /// Shared implementation behind <see cref="StartAsync"/> and the live-swap path of
    /// <see cref="RestartWithPresetAsync"/>. When <paramref name="isRestart"/> is true, the
    /// provider's <see cref="IBackendProvider.RestartProcessAsync"/> is used instead of
    /// <see cref="IBackendProvider.StartProcessAsync"/> — for wrapper-backed providers this
    /// swaps the model on the already-connected wrapper without disturbing companion/support
    /// processes, instead of tearing everything down first.
    /// </summary>
    private async Task<bool> StartOrRestartAsync(ServerInstance instance, bool isRestart, CancellationToken cancellationToken)
    {
        var provider = GetOrCreateProvider(instance);

        // Re-configure the provider with the latest config from the database
        // in case it was updated while the server was stopped
        if (instance.Config is not null)
        {
            provider.Configure(await ResolveConfigDataAsync(instance.Config));
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

        // Memory group: stop least-recently-used idle servers sharing this one's device until
        // the new model fits the group's budget. The lock is held until this server is
        // persisted as Starting, so a concurrent start in the same group counts it as loaded.
        using (var room = await new MemoryGroupScheduler(_context, _limiter).MakeRoomAsync(instance, preset, StopAsync, cancellationToken))
        {
            foreach (var evicted in room.Evicted)
                await LogLifecycleEvent(evicted, ServerLogLevel.Info,
                    $"Unloaded server '{evicted.Name}' to make room in memory group '{instance.MemoryGroup}' for '{instance.Name}'.");
            if (!room.Fits)
                await LogLifecycleEvent(instance, ServerLogLevel.Warning,
                    $"Memory group '{instance.MemoryGroup}' is over budget: '{preset.Name}' needs ~{room.NeededBytes / (1024 * 1024)} MB, " +
                    $"{room.UsedBytes / (1024 * 1024)} of {room.BudgetBytes / (1024 * 1024)} MB is still held by busy or starting servers after unloading idle ones. Starting anyway.");

            // Set status to Starting and persist immediately — then offload to background task
            instance.Status = ServerStatus.Starting;
            await _context.SaveChangesAsync(cancellationToken);
        }
        _limiter.Touch(instance.Id);
        await LogLifecycleEvent(instance, ServerLogLevel.Info,
            $"{(isRestart ? "Restarting" : "Starting")} server '{instance.Name}' on port {instance.Port}...");

        var progressEvent = new StartupProgressEvent
        {
            InstanceId = instance.Id,
            EventType = StartupEventType.Starting,
            Message = $"Server '{instance.Name}' is {(isRestart ? "restarting" : "starting")}...",
            ElapsedSeconds = 0
        };
        await BroadcastStartupProgress(progressEvent);

        // Offload the actual startup to a background task so the API returns immediately
        var scopeFactory = _scopeFactory;
        var limiter = _limiter;
        Func<ModelPreset, int?, Func<StartupProgressEvent, Task>?, CancellationToken, Task<bool>> invokeProvider =
            isRestart ? provider.RestartProcessAsync : provider.StartProcessAsync;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();

                bool started = await invokeProvider(
                    preset!, instance.Port,
                    async (e) =>
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
                                // Idle-unload clock starts once the model is actually loaded.
                                limiter.Touch(instance.Id);
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
                    // Best-effort teardown: a failed start may still have a wrapper process
                    // connected (it launched fine, but the server it was told to run never
                    // came up healthy) — leaving it running would orphan it, since nothing else
                    // is watching an instance stuck in Error.
                    try { await provider.StopProcessAsync(cancellationToken); } catch { /* best effort */ }

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
                // Same rationale as above — an exception during startup can still leave a
                // wrapper process connected and running; tear it down rather than orphan it.
                try { await provider.StopProcessAsync(cancellationToken); } catch { /* best effort */ }

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
        if (instance.Status != ServerStatus.Running && instance.Status != ServerStatus.Error)
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
        var instance = await _context.ServerInstances
            .Include(s => s.Config)
            .FirstOrDefaultAsync(s => s.Id == instanceId)
            ?? throw new KeyNotFoundException($"Server instance {instanceId} not found.");
        _ = await _context.ModelPresets.FindAsync(presetId) ?? throw new ArgumentException("Preset not found.", nameof(presetId));

        instance.ActivePresetId = presetId;
        await _context.SaveChangesAsync();

        if (instance.Status != ServerStatus.Running)
            return await StartAsync(instanceId, cancellationToken);

        // Live-swap the model without a full stop, so companion/support processes (e.g. a GPU
        // VRAM keeper) aren't disturbed by the preset change.
        return await StartOrRestartAsync(instance, isRestart: true, cancellationToken);
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
        // Return the tracked entity directly so callers can modify runtime state (IsBusy) 
        // and have it persist within the same request scope.
        var instance = await _context.ServerInstances.FindAsync(instanceId);
        return instance;
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

        config.InstallFolderPath = configData.InstallFolderPath;
        config.CompanionAppPath = configData.CompanionAppPath;
        config.EnvironmentSetupCommand = configData.EnvironmentSetupCommand;
        config.EngineBuildId = configData.EngineBuildId;

        await _context.SaveChangesAsync();
        return config;
    }

    /// <summary>
    /// Turns a persisted <see cref="BackendConfig"/> into the DTO the provider is configured with,
    /// resolving <see cref="BackendConfig.EngineBuildId"/> to the managed build's install folder
    /// (falling back to the manually-entered folder path when no build is bound or it's not ready).
    /// </summary>
    private async Task<BackendConfigData> ResolveConfigDataAsync(BackendConfig config)
    {
        var folderPath = config.InstallFolderPath;

        if (config.EngineBuildId is { } buildId)
        {
            var build = await _context.EngineBuilds.FindAsync(buildId);
            if (build is { Status: Models.EngineBuildStatus.Ready } && !string.IsNullOrWhiteSpace(build.InstallPath))
                folderPath = build.InstallPath;
        }

        return new BackendConfigData
        {
            InstallFolderPath = folderPath,
            CompanionAppPath = config.CompanionAppPath,
            EnvironmentSetupCommand = config.EnvironmentSetupCommand,
            EngineBuildId = config.EngineBuildId,
        };
    }

    public async Task RemoveInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await _context.ServerInstances.FindAsync(instanceId);
        if (instance is not null && (instance.Status == ServerStatus.Running || instance.Status == ServerStatus.Error))
            await StopAsync(instanceId, cancellationToken);

        if (instance is not null)
        {
            _context.ServerInstances.Remove(instance);
            await _context.SaveChangesAsync();

            // Drop the server's memory group if nothing else references it any more.
            if (instance.MemoryGroup is { } groupName
                && !await _context.ServerInstances.AnyAsync(s => s.MemoryGroup == groupName, cancellationToken)
                && await _context.MemoryGroups.FindAsync([groupName], cancellationToken) is { } group)
            {
                _context.MemoryGroups.Remove(group);
                await _context.SaveChangesAsync();
            }
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
            provider.Configure(await ResolveConfigDataAsync(instance.Config));
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
            return provider!;

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
