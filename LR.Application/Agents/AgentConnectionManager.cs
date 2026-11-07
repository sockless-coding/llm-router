using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using LR.Core.Agents;
using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Wrapper;

namespace LR.Application.Agents;

/// <summary>
/// Owns every live agent connection: reads what agents send, relays wrapper traffic between
/// agents and the providers' process managers (as <see cref="IRemoteWrapperConnector"/>), answers
/// path checks, pushes updates, and hands job progress/results to <see cref="AgentDeploymentService"/>.
/// </summary>
public sealed class AgentConnectionManager : IRemoteWrapperConnector
{
    private const int MaxActivityEntries = 200;
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, AgentConnection> _connections = new();
    private readonly ConcurrentDictionary<Guid, RemoteWrapperTransport> _transports = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RemoteWrapperTransport?>> _pendingAttaches = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<List<AgentPathStatus>>> _pendingPathChecks = new();
    private readonly ConcurrentDictionary<Guid, LinkedList<AgentActivity>> _activity = new();

    /// <summary>Last automatic update pushed per agent, so one that comes back unchanged isn't pushed again in a loop.</summary>
    private readonly ConcurrentDictionary<Guid, (string Version, DateTime At)> _autoUpdates = new();
    private static readonly TimeSpan AutoUpdateRetryAfter = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider _services;
    private readonly AgentPackageCatalog _packages;
    private readonly ILogger<AgentConnectionManager> _logger;

    public AgentConnectionManager(IServiceScopeFactory scopeFactory, IServiceProvider services, AgentPackageCatalog packages, ILogger<AgentConnectionManager> logger)
    {
        _scopeFactory = scopeFactory;
        _services = services;
        _packages = packages;
        _logger = logger;
    }

    private AgentDeploymentService Deployment => _services.GetRequiredService<AgentDeploymentService>();

    // ==================== QUERIES ====================

    public bool IsAgentOnline(Guid agentId) => _connections.TryGetValue(agentId, out var c) && c.IsOpen;

    public AgentConnection? GetConnection(Guid agentId) =>
        _connections.TryGetValue(agentId, out var c) && c.IsOpen ? c : null;

    public IReadOnlyList<AgentActivity> GetActivity(Guid agentId)
    {
        if (!_activity.TryGetValue(agentId, out var list)) return [];
        lock (list) return list.Reverse().ToList();
    }

    public void Log(Guid agentId, string level, string message)
    {
        var list = _activity.GetOrAdd(agentId, _ => new LinkedList<AgentActivity>());
        lock (list)
        {
            list.AddLast(new AgentActivity(DateTime.UtcNow, level, message));
            while (list.Count > MaxActivityEntries) list.RemoveFirst();
        }
    }

    // ==================== SENDING ====================

    /// <summary>Sends to a connected agent; throws <see cref="InvalidOperationException"/> when it's offline.</summary>
    public async Task SendAsync(Guid agentId, AgentMessage message, CancellationToken ct = default)
    {
        var connection = GetConnection(agentId) ?? throw new InvalidOperationException("The agent is offline.");
        try
        {
            await connection.SendAsync(message, ct);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>Asks the agent which of <paramref name="paths"/> exist. Null when it's offline or doesn't answer.</summary>
    public async Task<List<AgentPathStatus>?> CheckPathsAsync(Guid agentId, IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return [];
        var requestId = Guid.NewGuid();
        var tcs = new TaskCompletionSource<List<AgentPathStatus>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPathChecks[requestId] = tcs;
        try
        {
            await SendAsync(agentId, new CheckPathsMessage { RequestId = requestId, Paths = paths.ToList() }, ct);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            return null;
        }
        finally
        {
            _pendingPathChecks.TryRemove(requestId, out _);
        }
    }

    /// <summary>Pushes the router's packaged agent version to the agent if it runs something else.</summary>
    public async Task<string?> RequestUpdateAsync(Guid agentId, bool force, CancellationToken ct = default)
    {
        var connection = GetConnection(agentId) ?? throw new InvalidOperationException("The agent is offline.");
        var rid = connection.Hello?.Rid;
        var package = _packages.Find(rid);
        var version = _packages.LatestVersion;
        if (package is null || version is null)
            throw new InvalidOperationException($"This router has no agent package for {rid ?? "this platform"} — see the Agents page.");
        if (!force && string.Equals(connection.Hello?.Version, version, StringComparison.OrdinalIgnoreCase))
            return null;

        connection.UpdateRequestedFor = version;
        await connection.SendAsync(new UpdateAgentMessage
        {
            Version = version,
            Sha256 = package.Value.Entry.Sha256,
            Size = package.Value.Entry.Size,
        }, ct);
        Log(agentId, "info", $"Asked the agent to update from {connection.Hello?.Version} to {version}.");
        return version;
    }

    // ==================== WRAPPER RELAY (IRemoteWrapperConnector) ====================

    public async Task<IWrapperTransport?> AttachAsync(Guid agentId, Guid instanceId, bool launch, CancellationToken ct)
    {
        var connection = GetConnection(agentId);
        if (connection is null) return null;

        var tcs = new TaskCompletionSource<RemoteWrapperTransport?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_pendingAttaches.TryRemove(instanceId, out var stale))
            stale.TrySetResult(null);
        _pendingAttaches[instanceId] = tcs;

        try
        {
            await connection.SendAsync(new AttachWrapperMessage { InstanceId = instanceId, Launch = launch }, ct);
            return await tcs.Task.WaitAsync(AttachTimeout, ct);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("The agent didn't answer the request to attach to this server's wrapper.");
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            _pendingAttaches.TryRemove(new KeyValuePair<Guid, TaskCompletionSource<RemoteWrapperTransport?>>(instanceId, tcs));
        }
    }

    private void OnTransportDisposed(RemoteWrapperTransport transport) =>
        _transports.TryRemove(new KeyValuePair<Guid, RemoteWrapperTransport>(transport.InstanceId, transport));

    // ==================== CONNECTION LIFECYCLE ====================

    /// <summary>Serves one accepted agent WebSocket until it closes.</summary>
    public async Task RunAsync(Guid agentId, WebSocket socket, string remoteAddress, CancellationToken ct)
    {
        var connection = new AgentConnection(agentId, socket, remoteAddress);
        if (_connections.TryGetValue(agentId, out var previous))
        {
            Log(agentId, "warning", "A new connection replaced the previous one.");
            await previous.CloseAsync("replaced by a newer connection");
        }
        _connections[agentId] = connection;
        Log(agentId, "info", $"Connected from {remoteAddress}.");

        try
        {
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                connection.LastMessageAt = DateTime.UtcNow;
                AgentMessage? parsed;
                try
                {
                    parsed = AgentProtocol.Deserialize(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Unparseable message from agent {AgentId}.", agentId);
                    continue;
                }
                if (parsed is null) continue;

                try
                {
                    await HandleAsync(connection, parsed, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Handling {Type} from agent {AgentId} failed.", parsed.GetType().Name, agentId);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            // Connection dropped.
        }
        finally
        {
            if (_connections.TryGetValue(agentId, out var current) && ReferenceEquals(current, connection))
            {
                _connections.TryRemove(agentId, out _);
                Log(agentId, "warning", "Disconnected.");
                await MarkDisconnectedAsync(agentId);
            }

            // Every relayed wrapper of this agent loses its path; their process managers see the
            // transport close and re-attach when the agent is back. Backends keep running meanwhile.
            foreach (var transport in _transports.Values.Where(t => ReferenceEquals(t.Connection, connection)).ToList())
            {
                transport.Close();
                _transports.TryRemove(new KeyValuePair<Guid, RemoteWrapperTransport>(transport.InstanceId, transport));
            }
        }
    }

    private async Task HandleAsync(AgentConnection connection, AgentMessage message, CancellationToken ct)
    {
        if (_logger.IsEnabled(LogLevel.Debug) && message is not (WrapperFrameMessage { Frame: OutputLineEvent } or JobProgressMessage))
            _logger.LogDebug("Agent {AgentId} -> router: {Type}{Frame}", connection.AgentId, message.GetType().Name,
                message is WrapperFrameMessage f ? $" ({f.Frame.GetType().Name}, transport: {_transports.ContainsKey(f.InstanceId)})" : "");
        switch (message)
        {
            case AgentHelloMessage hello:
                await OnHelloAsync(connection, hello, ct);
                break;

            case AgentStatusMessage status:
                connection.SystemInfo = status.SystemInfo;
                await PersistStatusAsync(connection.AgentId, status.SystemInfo);
                break;

            case WrapperAttachedMessage attached:
            {
                var transport = new RemoteWrapperTransport(connection, attached.InstanceId, attached.Hello, OnTransportDisposed);
                if (_transports.TryGetValue(attached.InstanceId, out var old))
                    old.Close();
                _transports[attached.InstanceId] = transport;
                if (!_pendingAttaches.TryGetValue(attached.InstanceId, out var tcs) || !tcs.TrySetResult(transport))
                {
                    // Nobody is waiting any more (timed out) — don't leave it registered.
                    await transport.DisposeAsync();
                }
                break;
            }

            case WrapperAttachFailedMessage failed:
                if (_pendingAttaches.TryGetValue(failed.InstanceId, out var failedTcs))
                {
                    if (failed.NotRunning) failedTcs.TrySetResult(null);
                    else failedTcs.TrySetException(new InvalidOperationException($"Agent: {failed.Error}"));
                }
                if (!failed.NotRunning)
                    Log(connection.AgentId, "error", $"Wrapper attach failed: {failed.Error}");
                break;

            case WrapperFrameMessage frame:
                if (_transports.TryGetValue(frame.InstanceId, out var target) && ReferenceEquals(target.Connection, connection))
                    target.Deliver(frame.Frame);
                break;

            case WrapperClosedMessage closed:
                if (_transports.TryRemove(closed.InstanceId, out var gone))
                    gone.Close();
                break;

            case JobProgressMessage progress:
                await Deployment.OnJobProgressAsync(connection.AgentId, progress);
                break;

            case JobCompletedMessage completed:
                await Deployment.OnJobCompletedAsync(connection.AgentId, completed);
                break;

            case AgentPathsReport report:
                if (_pendingPathChecks.TryGetValue(report.RequestId, out var pathsTcs))
                    pathsTcs.TrySetResult(report.Paths);
                break;

            case AgentLogMessage log:
                Log(connection.AgentId, log.Level, log.Message);
                break;
        }
    }

    private async Task OnHelloAsync(AgentConnection connection, AgentHelloMessage hello, CancellationToken ct)
    {
        connection.Hello = hello;
        connection.SystemInfo = hello.SystemInfo;

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var agent = await context.RemoteAgents.FindAsync([connection.AgentId], ct);
        if (agent is null)
        {
            await connection.CloseAsync("unknown agent");
            return;
        }

        agent.Version = hello.Version;
        agent.Rid = hello.Rid;
        agent.Os = hello.Os;
        agent.Arch = hello.Arch;
        agent.Hostname = hello.Hostname;
        agent.OsDescription = hello.OsDescription;
        agent.DataDirectory = hello.DataDirectory;
        agent.LastRemoteAddress = connection.RemoteAddress;
        agent.SystemInfoJson = JsonSerializer.Serialize(hello.SystemInfo, AgentProtocol.JsonOptions);
        agent.LastSeenAt = DateTime.UtcNow;
        agent.LastConnectedAt = DateTime.UtcNow;
        agent.LastError = null;
        await context.SaveChangesAsync(ct);

        var library = await scope.ServiceProvider.GetRequiredService<IModelLibrarySettingsService>().GetAsync(ct);
        var builds = await scope.ServiceProvider.GetRequiredService<IEngineBuildSettingsService>().GetAsync(ct);
        await connection.SendAsync(new AgentWelcomeMessage
        {
            AgentId = agent.Id,
            Name = agent.Name,
            LatestVersion = _packages.LatestVersion,
            HuggingFaceToken = library.HuggingFaceApiToken,
            GitHubToken = builds.GitHubApiToken,
        }, ct);
        Log(agent.Id, "info", $"Agent {hello.Version} on {hello.OsDescription ?? hello.Os} ({hello.Rid}), {hello.Wrappers.Count} wrapper(s) running.");

        if (agent.AutoUpdate && _packages.LatestVersion is { } latest
            && !string.Equals(hello.Version, latest, StringComparison.OrdinalIgnoreCase)
            && _packages.Find(hello.Rid) is not null)
        {
            if (_autoUpdates.TryGetValue(agent.Id, out var last) && last.Version == latest && DateTime.UtcNow - last.At < AutoUpdateRetryAfter)
            {
                Log(agent.Id, "warning", $"Still on {hello.Version} after being sent {latest}; not retrying for a while (see the agent's log).");
            }
            else
            {
                _autoUpdates[agent.Id] = (latest, DateTime.UtcNow);
                try { await RequestUpdateAsync(agent.Id, force: false, ct); }
                catch (Exception ex) { Log(agent.Id, "error", $"Couldn't push the update: {ex.Message}"); }
            }
        }
        else if (string.Equals(hello.Version, _packages.LatestVersion, StringComparison.OrdinalIgnoreCase))
        {
            _autoUpdates.TryRemove(agent.Id, out _);
        }

        // Re-attach servers, reconcile files and interrupted jobs in the background so the read
        // loop is free to receive the attach replies this triggers.
        _ = Task.Run(() => Deployment.OnAgentConnectedAsync(agent.Id, hello, CancellationToken.None));
    }

    private async Task PersistStatusAsync(Guid agentId, AgentSystemInfo info)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        if (await context.RemoteAgents.FindAsync(agentId) is { } agent)
        {
            agent.SystemInfoJson = JsonSerializer.Serialize(info, AgentProtocol.JsonOptions);
            agent.LastSeenAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
        }
    }

    private async Task MarkDisconnectedAsync(Guid agentId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
            if (await context.RemoteAgents.FindAsync(agentId) is { } agent)
            {
                agent.LastSeenAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't record the disconnect of agent {AgentId}.", agentId);
        }
    }

    /// <summary>Drops an agent's live connection (used when it's disabled or deleted).</summary>
    public async Task DisconnectAsync(Guid agentId, string reason)
    {
        if (_connections.TryGetValue(agentId, out var connection))
            await connection.CloseAsync(reason);
    }
}
