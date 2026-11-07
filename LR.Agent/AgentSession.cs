using System.Net.WebSockets;
using System.Text;

using LR.Core.Agents;

namespace LR.Agent;

/// <summary>
/// The send side of the current router connection, shared by the relay and the job runner. Sends
/// are serialized (a WebSocket allows one writer at a time); while disconnected they're dropped —
/// except job completions, which are kept and replayed on the next connection so a download or
/// install that finished during an outage still lands on the router.
/// </summary>
public sealed class AgentSession
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<Guid, JobCompletedMessage> _undeliveredCompletions = new();
    private readonly object _completionsLock = new();
    private readonly ILogger<AgentSession> _logger;
    private WebSocket? _socket;

    public AgentSession(ILogger<AgentSession> logger) => _logger = logger;

    public bool IsConnected => _socket is { State: WebSocketState.Open };

    /// <summary>Tokens the router handed out in its welcome, for downloads made on its behalf.</summary>
    public string? HuggingFaceToken { get; set; }
    public string? GitHubToken { get; set; }

    public void Attach(WebSocket socket) => _socket = socket;

    public void Detach() => _socket = null;

    public async Task<bool> SendAsync(AgentMessage message, CancellationToken ct = default)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open })
            return false;

        var bytes = Encoding.UTF8.GetBytes(AgentProtocol.Serialize(message));
        await _sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogWarning("Send to router failed: {Message}", ex.Message);
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Sends a job completion, keeping it for replay if the router can't be reached right now.</summary>
    public async Task SendCompletionAsync(JobCompletedMessage message)
    {
        lock (_completionsLock) _undeliveredCompletions[message.JobId] = message;
        if (await SendAsync(message))
            lock (_completionsLock) _undeliveredCompletions.Remove(message.JobId);
    }

    /// <summary>Replays completions that couldn't be delivered earlier. Called right after the router's welcome.</summary>
    public async Task FlushCompletionsAsync(CancellationToken ct)
    {
        List<JobCompletedMessage> pending;
        lock (_completionsLock) pending = _undeliveredCompletions.Values.ToList();
        foreach (var message in pending)
        {
            if (!await SendAsync(message, ct)) return;
            lock (_completionsLock) _undeliveredCompletions.Remove(message.JobId);
        }
    }
}
