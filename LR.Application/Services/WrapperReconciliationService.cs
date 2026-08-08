using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Core.Wrapper;

namespace LR.Application.Services;

/// <summary>
/// Boot-time reconciliation pass: scans the wrapper state directory for wrapper processes that
/// outlived a previous router process and re-attaches to any that are still alive, so the DB
/// and UI reflect reality instead of stale pre-restart state. Invoked explicitly and awaited
/// from Program.cs before Kestrel starts accepting connections — not a BackgroundService.
/// </summary>
public class WrapperReconciliationService
{
    private readonly LRDbContext _context;
    private readonly IBackendProviderFactory _providerFactory;
    private readonly ProviderRegistry _registry;
    private readonly ILogger<WrapperReconciliationService> _logger;

    public WrapperReconciliationService(
        LRDbContext context,
        IBackendProviderFactory providerFactory,
        ProviderRegistry registry,
        ILogger<WrapperReconciliationService> logger)
    {
        _context = context;
        _providerFactory = providerFactory;
        _registry = registry;
        _logger = logger;
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        string stateDir = WrapperConventions.GetDefaultStateDirectory(AppDomain.CurrentDomain.BaseDirectory);
        if (!Directory.Exists(stateDir))
            return;

        var stateFiles = Directory.EnumerateFiles(stateDir, "*.json").ToList();
        if (stateFiles.Count == 0)
            return;

        _logger.LogInformation("Reconciling {Count} wrapper state file(s) from a previous router run.", stateFiles.Count);

        foreach (var path in stateFiles)
        {
            try
            {
                await ReconcileOneAsync(path, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error reconciling wrapper state file {Path}.", path);
            }
        }
    }

    private async Task ReconcileOneAsync(string stateFilePath, CancellationToken ct)
    {
        Guid instanceId;
        try
        {
            instanceId = Guid.ParseExact(Path.GetFileNameWithoutExtension(stateFilePath), "N");
        }
        catch
        {
            _logger.LogWarning("Skipping malformed wrapper state file name: {Path}", stateFilePath);
            return;
        }

        var instance = await _context.ServerInstances
            .Include(s => s.Config)
            .FirstOrDefaultAsync(s => s.Id == instanceId, ct);

        if (instance is null)
        {
            _logger.LogInformation("Wrapper state file {Path} has no matching server instance; shutting it down.", stateFilePath);
            await ShutdownOrphanAsync(instanceId, stateFilePath, ct);
            return;
        }

        var provider = _providerFactory.Create(instance.Engine);
        if (provider is null)
        {
            _logger.LogWarning("No backend provider available for engine {Engine}; cannot reconcile instance {InstanceId}.",
                instance.Engine, instanceId);
            return;
        }

        if (instance.Config is not null)
        {
            provider.Configure(new BackendConfigData
            {
                LlamaCppExecutableFolderPath = instance.Config.LlamaCppExecutableFolderPath,
                CompanionAppPath = instance.Config.CompanionAppPath,
                EnvironmentSetupCommand = instance.Config.EnvironmentSetupCommand,
            });
        }
        provider.SetServerInstance(instance);

        bool wasRunning = instance.Status == ServerStatus.Running;
        instance.Status = ServerStatus.Reconnecting;
        await _context.SaveChangesAsync(ct);

        bool reconnected;
        try
        {
            reconnected = await provider.TryReconnectAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reconnecting wrapper for instance {InstanceId}.", instanceId);
            reconnected = false;
        }

        if (reconnected)
        {
            _registry.Register(instance.Id, provider);
            bool healthy = await provider.HealthCheckAsync(ct);
            instance.Status = ServerStatus.Running;
            instance.IsHealthy = healthy;
            _logger.LogInformation("Reattached to running server '{Name}' ({InstanceId}), healthy={Healthy}.",
                instance.Name, instance.Id, healthy);
        }
        else
        {
            instance.Status = wasRunning ? ServerStatus.Error : ServerStatus.Idle;
            instance.IsHealthy = false;
            if (wasRunning)
            {
                instance.LastErrorMessage = "Server was not found running after router restart.";
                instance.LastErrorTime = DateTime.UtcNow;
            }
            _logger.LogInformation("No live server found to reattach for '{Name}' ({InstanceId}); status set to {Status}.",
                instance.Name, instance.Id, instance.Status);
        }

        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Shuts down a wrapper whose server instance no longer exists in the DB (deleted while the
    /// router was down). There's no config left to manage it under, so it's torn down
    /// unconditionally regardless of whether its server is still running.
    /// </summary>
    private async Task ShutdownOrphanAsync(Guid instanceId, string stateFilePath, CancellationToken ct)
    {
        WrapperStateFile? state;
        try
        {
            var json = await File.ReadAllTextAsync(stateFilePath, ct);
            state = JsonSerializer.Deserialize<WrapperStateFile>(json);
        }
        catch
        {
            state = null;
        }

        if (state is null)
        {
            TryDeleteStateFile(stateFilePath);
            return;
        }

        if (!IsWrapperProcessAlive(state))
        {
            TryDeleteStateFile(stateFilePath);
            return;
        }

        try
        {
            using var pipeClient = new NamedPipeClientStream(".", state.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipeClient.ConnectAsync(5000, ct);
            await using var connection = new WrapperPipeConnection(pipeClient);
            await connection.ReceiveAsync(ct); // discard the initial Hello
            await connection.SendAsync(new StopCommand { StopCompanion = true, ShutdownWrapper = true }, ct);
            await connection.ReceiveAsync(ct); // discard the ack — best effort
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanly shut down orphaned wrapper for instance {InstanceId}; it may need manual cleanup.", instanceId);
        }

        TryDeleteStateFile(stateFilePath);
    }

    private static bool IsWrapperProcessAlive(WrapperStateFile state)
    {
        try
        {
            var proc = Process.GetProcessById(state.WrapperPid);
            if (proc.HasExited) return false;

            var startTimeUtc = proc.StartTime.ToUniversalTime();
            return Math.Abs((startTimeUtc - state.WrapperStartedAtUtc).TotalSeconds) <= 5;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteStateFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
