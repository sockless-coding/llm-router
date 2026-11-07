using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;

using LR.Core.Agents;

namespace LR.Agent;

/// <summary>
/// The agent's main loop: enroll if needed, keep a WebSocket to the router open (reconnecting with
/// backoff), say hello, and dispatch whatever the router sends to the relay, the job runner or the
/// updater. Backends and jobs keep going while the connection is down.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private readonly AgentConfig _config;
    private readonly AgentSession _session;
    private readonly WrapperRelay _relay;
    private readonly JobRunner _jobs;
    private readonly SelfUpdater _updater;
    private readonly ILogger<AgentWorker> _logger;

    public AgentWorker(AgentConfig config, AgentSession session, WrapperRelay relay, JobRunner jobs, SelfUpdater updater, ILogger<AgentWorker> logger)
    {
        _config = config;
        _session = session;
        _relay = relay;
        _jobs = jobs;
        _updater = updater;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LR agent {Version} ({Rid}) starting; router {Url}, data in {DataDir}.",
            AgentInfo.Version, AgentProtocol.CurrentRid(), _config.RouterUrl, _config.ResolvedDataDirectory);

        await _relay.RecoverAsync(stoppingToken);

        var backoff = TimeSpan.FromSeconds(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_config.IsEnrolled)
                    await EnrollAsync(stoppingToken);

                await RunConnectionAsync(stoppingToken);
                backoff = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (AgentRejectedException ex)
            {
                _logger.LogError("The router refused this agent: {Reason}", ex.Message);
                if (ex.Unknown && !string.IsNullOrEmpty(_config.EnrollmentToken))
                {
                    _logger.LogInformation("Enrolling again with the configured enrollment token.");
                    _config.AgentId = null;
                    _config.Secret = null;
                    continue;
                }
                backoff = TimeSpan.FromMinutes(1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Router connection failed: {Message}", ex.Message);
            }
            finally
            {
                _session.Detach();
            }

            try { await Task.Delay(backoff, stoppingToken); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }

    private async Task EnrollAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.EnrollmentToken))
            throw new AgentRejectedException("Not enrolled and no enrollment token configured — rerun the install command from the router's Agents page.", unknown: false);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var response = await http.PostAsJsonAsync(new Uri(new Uri(_config.RouterUrl), AgentProtocol.EnrollPath), new AgentEnrollRequest
        {
            EnrollmentToken = _config.EnrollmentToken,
            MachineId = _config.MachineId,
            Hostname = Environment.MachineName,
            Rid = AgentProtocol.CurrentRid(),
        }, AgentProtocol.JsonOptions, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new AgentRejectedException("The enrollment token was rejected — copy a fresh install command from the router's Agents page.", unknown: false);
        response.EnsureSuccessStatusCode();

        var enrolled = await response.Content.ReadFromJsonAsync<AgentEnrollResponse>(AgentProtocol.JsonOptions, ct)
            ?? throw new InvalidOperationException("Empty enrollment response.");
        _config.AgentId = enrolled.AgentId;
        _config.Secret = enrolled.Secret;
        _config.EnrollmentToken = null; // single-purpose: the agent's own secret takes over from here
        _config.Save();
        _logger.LogInformation("Enrolled with the router as '{Name}' ({AgentId}).", enrolled.Name, enrolled.AgentId);
    }

    private async Task RunConnectionAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + _config.Secret);
        socket.Options.SetRequestHeader(AgentProtocol.AgentIdHeader, _config.AgentId!.Value.ToString());
        socket.Options.SetRequestHeader(AgentProtocol.ProtocolHeader, AgentProtocol.Version.ToString());
        socket.Options.CollectHttpResponseDetails = true;

        var uri = new UriBuilder(new Uri(new Uri(_config.RouterUrl), AgentProtocol.ConnectPath));
        uri.Scheme = uri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";

        try
        {
            await socket.ConnectAsync(uri.Uri, ct);
        }
        catch (WebSocketException) when (socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            throw new AgentRejectedException("This agent isn't known to the router (it was deleted, or the router's database was reset).", unknown: true);
        }
        catch (WebSocketException) when (socket.HttpStatusCode == HttpStatusCode.Forbidden)
        {
            throw new AgentRejectedException("This agent is disabled on the router.", unknown: false);
        }
        catch (WebSocketException) when (socket.HttpStatusCode == HttpStatusCode.Conflict)
        {
            throw new AgentRejectedException("The router speaks a different agent protocol version — reinstall the agent from the router's Agents page.", unknown: false);
        }

        _logger.LogInformation("Connected to the router.");
        _session.Attach(socket);

        await _session.SendAsync(new AgentHelloMessage
        {
            Version = AgentInfo.Version,
            Rid = AgentProtocol.CurrentRid(),
            Os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            Arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Hostname = Environment.MachineName,
            OsDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            DataDirectory = _config.ResolvedDataDirectory,
            SystemInfo = SystemInfoCollector.Collect(_config.ResolvedDataDirectory),
            Wrappers = _relay.Snapshot(),
            ActiveJobs = _jobs.ActiveJobIds(),
        }, ct);

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? statusLoop = null;
        try
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("Router closed the connection: {Reason}", result.CloseStatusDescription);
                        return;
                    }
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                AgentMessage? parsed;
                try { parsed = AgentProtocol.Deserialize(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Ignoring a message the agent couldn't parse (router newer than agent?).");
                    continue;
                }

                if (parsed is AgentWelcomeMessage welcome)
                {
                    _session.HuggingFaceToken = welcome.HuggingFaceToken;
                    _session.GitHubToken = welcome.GitHubToken;
                    _logger.LogInformation("Router welcomed this agent as '{Name}'.", welcome.Name);
                    await _session.FlushCompletionsAsync(ct);
                    statusLoop ??= RunStatusLoopAsync(TimeSpan.FromSeconds(Math.Max(5, welcome.StatusIntervalSeconds)), connectionCts.Token);
                    continue;
                }

                // Wrapper frames are handled inline so each wrapper sees commands in the order the
                // router sent them. Attaching (which may launch a process) and updating run in the
                // background — the router only sends a wrapper frames once it has seen it attached.
                if (parsed is AttachWrapperMessage or UpdateAgentMessage)
                    _ = DispatchAsync(parsed, ct);
                else if (parsed is not null)
                    await DispatchAsync(parsed, ct);
            }
        }
        finally
        {
            connectionCts.Cancel();
            if (statusLoop is not null)
                try { await statusLoop; } catch { /* stopping */ }
            _session.Detach();
            if (socket.State == WebSocketState.Open)
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "agent stopping", CancellationToken.None); } catch { }
        }
    }

    private async Task DispatchAsync(AgentMessage message, CancellationToken ct)
    {
        try
        {
            switch (message)
            {
                case AttachWrapperMessage attach:
                    await _relay.AttachAsync(attach.InstanceId, attach.Launch, ct);
                    break;
                case WrapperFrameMessage frame:
                    await _relay.ForwardAsync(frame.InstanceId, frame.Frame, ct);
                    break;
                case AgentJobMessage job:
                    _jobs.Start(job);
                    break;
                case CancelJobMessage cancel:
                    _jobs.Cancel(cancel.JobId);
                    break;
                case CheckPathsMessage check:
                    await _session.SendAsync(new AgentPathsReport
                    {
                        RequestId = check.RequestId,
                        Paths = check.Paths.Select(JobRunner.Stat).ToList(),
                    }, ct);
                    break;
                case UpdateAgentMessage update:
                    await _updater.UpdateAsync(update, ct);
                    break;
                case RestartAgentMessage:
                    await _session.SendAsync(new AgentLogMessage { Message = "Restarting on the router's request." }, ct);
                    _updater.Restart();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling {Type} failed.", message.GetType().Name);
            await _session.SendAsync(new AgentLogMessage { Level = "error", Message = $"{message.GetType().Name} failed: {ex.Message}" }, ct);
        }
    }

    private async Task RunStatusLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            await _session.SendAsync(new AgentStatusMessage
            {
                SystemInfo = SystemInfoCollector.Collect(_config.ResolvedDataDirectory),
            }, ct);
        }
    }
}

/// <summary>The router refused the agent's identity or enrollment.</summary>
public sealed class AgentRejectedException : Exception
{
    public AgentRejectedException(string message, bool unknown) : base(message) => Unknown = unknown;

    /// <summary>The router doesn't know this agent (as opposed to knowing and refusing it).</summary>
    public bool Unknown { get; }
}
