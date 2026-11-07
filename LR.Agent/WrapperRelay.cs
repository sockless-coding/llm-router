using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

using LR.Core.Agents;
using LR.Core.Wrapper;

namespace LR.Agent;

/// <summary>
/// Bridges the router's wrapper protocol to wrapper processes on this machine. Each server the
/// router runs here gets its own <c>LR.Agent wrapper</c> process (launched on demand) that owns the
/// backend — exactly what the router does locally with LR.Wrapper. The relay holds the named-pipe
/// connection to each and forwards messages both ways tagged with the server's id. Wrappers are
/// independent processes, so the agent can restart or update itself without stopping backends; on
/// start it finds them again through their state files.
/// </summary>
public sealed class WrapperRelay
{
    private const int PipeConnectTimeoutMs = 15_000;

    private readonly ConcurrentDictionary<Guid, Link> _links = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _attachLocks = new();
    private readonly AgentSession _session;
    private readonly ILogger<WrapperRelay> _logger;
    private readonly string _stateDir;

    public WrapperRelay(AgentConfig config, AgentSession session, ILogger<WrapperRelay> logger)
    {
        _session = session;
        _logger = logger;
        _stateDir = Path.Combine(config.ResolvedDataDirectory, "wrappers");
    }

    /// <summary>Reconnects to wrappers that outlived a previous agent process; drops stale state files.</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_stateDir)) return;
        foreach (var file in Directory.EnumerateFiles(_stateDir, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var instanceId))
                continue;
            try
            {
                if (await TryConnectExistingAsync(instanceId, ct) is { } hello)
                    _logger.LogInformation("Recovered wrapper for server {InstanceId} (server running: {Running}).", instanceId, hello.ServerRunning);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not recover the wrapper for {InstanceId}.", instanceId);
            }
        }
    }

    /// <summary>The wrappers alive right now, for the hello.</summary>
    public List<AgentWrapperInfo> Snapshot() => _links.Values
        .Select(l => new AgentWrapperInfo { InstanceId = l.InstanceId, ServerRunning = l.ServerRunning, Port = l.Port })
        .ToList();

    public async Task AttachAsync(Guid instanceId, bool launch, CancellationToken ct)
    {
        var gate = _attachLocks.GetOrAdd(instanceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_links.TryGetValue(instanceId, out var existing))
            {
                // Already connected: ask for a fresh hello; the reader turns it into the "attached" reply.
                existing.PendingAttach = true;
                try
                {
                    await existing.Connection.SendAsync(new PingCommand(), ct);
                    return;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    existing.PendingAttach = false;
                    DropLink(existing);
                }
            }

            var hello = await TryConnectExistingAsync(instanceId, ct);
            if (hello is null && launch)
                hello = await LaunchAsync(instanceId, ct);

            if (hello is null)
            {
                await _session.SendAsync(new WrapperAttachFailedMessage
                {
                    InstanceId = instanceId,
                    NotRunning = true,
                    Error = "No wrapper is running for this server.",
                }, ct);
                return;
            }

            await _session.SendAsync(new WrapperAttachedMessage { InstanceId = instanceId, Hello = hello }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Attaching to the wrapper of {InstanceId} failed.", instanceId);
            await _session.SendAsync(new WrapperAttachFailedMessage { InstanceId = instanceId, Error = ex.Message }, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ForwardAsync(Guid instanceId, WrapperMessage frame, CancellationToken ct)
    {
        _logger.LogDebug("Router -> wrapper {InstanceId}: {Type} (linked: {Linked})", instanceId, frame.GetType().Name, _links.ContainsKey(instanceId));
        if (_links.TryGetValue(instanceId, out var link))
        {
            try
            {
                await link.Connection.SendAsync(frame, ct);
                return;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                DropLink(link);
            }
        }

        // Nothing to deliver to — tell the router so it doesn't wait for an ack that won't come.
        await _session.SendAsync(new WrapperClosedMessage { InstanceId = instanceId }, ct);
    }

    private async Task<HelloEvent?> TryConnectExistingAsync(Guid instanceId, CancellationToken ct)
    {
        var stateFile = WrapperConventions.GetStateFilePath(_stateDir, instanceId);
        if (!File.Exists(stateFile)) return null;

        WrapperStateFile? state;
        try { state = JsonSerializer.Deserialize<WrapperStateFile>(await File.ReadAllTextAsync(stateFile, ct)); }
        catch { state = null; }

        if (state is null || !WrapperServer.IsAlive(state))
        {
            TryDelete(stateFile);
            return null;
        }

        return await ConnectAsync(instanceId, state.PipeName, ct);
    }

    private async Task<HelloEvent?> LaunchAsync(Guid instanceId, CancellationToken ct)
    {
        Directory.CreateDirectory(_stateDir);
        var startInfo = new ProcessStartInfo
        {
            FileName = AgentInfo.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        startInfo.ArgumentList.Add("wrapper");
        startInfo.ArgumentList.Add("--instance-id");
        startInfo.ArgumentList.Add(instanceId.ToString());
        startInfo.ArgumentList.Add("--state-dir");
        startInfo.ArgumentList.Add(_stateDir);

        using (var process = Process.Start(startInfo))
            _logger.LogInformation("Launched wrapper (pid {Pid}) for server {InstanceId}.", process?.Id, instanceId);

        return await ConnectAsync(instanceId, WrapperConventions.GetPipeName(instanceId), ct)
            ?? throw new InvalidOperationException("The wrapper process started but never accepted a connection.");
    }

    private async Task<HelloEvent?> ConnectAsync(Guid instanceId, string pipeName, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(PipeConnectTimeoutMs, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            await pipe.DisposeAsync();
            _logger.LogWarning("Could not connect to the wrapper pipe {Pipe}: {Message}", pipeName, ex.Message);
            return null;
        }

        var connection = new WrapperPipeConnection(pipe);
        if (await connection.ReceiveAsync(ct) is not HelloEvent hello)
        {
            await connection.DisposeAsync();
            return null;
        }

        var link = new Link(instanceId, connection) { ServerRunning = hello.ServerRunning, Port = hello.Port };
        if (_links.TryRemove(instanceId, out var previous))
            DropLink(previous);
        _links[instanceId] = link;
        link.Reader = Task.Run(() => ReadLoopAsync(link));
        return hello;
    }

    private async Task ReadLoopAsync(Link link)
    {
        try
        {
            while (true)
            {
                var message = await link.Connection.ReceiveAsync(link.Cts.Token);
                if (message is null) break;

                switch (message)
                {
                    case HelloEvent hello:
                        link.ServerRunning = hello.ServerRunning;
                        link.Port = hello.Port;
                        if (link.PendingAttach)
                        {
                            link.PendingAttach = false;
                            await _session.SendAsync(new WrapperAttachedMessage { InstanceId = link.InstanceId, Hello = hello });
                            continue;
                        }
                        break;
                    case ProcessStartedEvent:
                        link.ServerRunning = true;
                        break;
                    case ProcessExitedEvent:
                        link.ServerRunning = false;
                        break;
                }

                var sent = await _session.SendAsync(new WrapperFrameMessage { InstanceId = link.InstanceId, Frame = message });
                if (message is not OutputLineEvent || !sent)
                    _logger.LogDebug("Wrapper {InstanceId} -> router: {Type} (sent: {Sent})", link.InstanceId, message.GetType().Name, sent);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
        {
            // Wrapper exited (normally after a full stop) or the pipe broke.
            _logger.LogDebug("Wrapper {InstanceId} read loop ended: {Message}", link.InstanceId, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Relaying the wrapper of {InstanceId} failed.", link.InstanceId);
        }

        if (_links.TryGetValue(link.InstanceId, out var current) && ReferenceEquals(current, link))
        {
            _links.TryRemove(link.InstanceId, out _);
            if (link.PendingAttach)
                await _session.SendAsync(new WrapperAttachFailedMessage { InstanceId = link.InstanceId, Error = "The wrapper went away while attaching." });
            else
                await _session.SendAsync(new WrapperClosedMessage { InstanceId = link.InstanceId });
        }
        await link.Connection.DisposeAsync();
    }

    private void DropLink(Link link)
    {
        _links.TryRemove(new KeyValuePair<Guid, Link>(link.InstanceId, link));
        link.Cts.Cancel();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private sealed class Link
    {
        public Link(Guid instanceId, WrapperPipeConnection connection)
        {
            InstanceId = instanceId;
            Connection = connection;
        }

        public Guid InstanceId { get; }
        public WrapperPipeConnection Connection { get; }
        public CancellationTokenSource Cts { get; } = new();
        public Task? Reader { get; set; }
        public volatile bool PendingAttach;
        public volatile bool ServerRunning;
        public int? Port { get; set; }
    }
}
