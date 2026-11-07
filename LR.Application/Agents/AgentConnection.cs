using System.Net.WebSockets;
using System.Text;

using LR.Core.Agents;

namespace LR.Application.Agents;

/// <summary>One live agent WebSocket (router side). Sends are serialized; reading is done by the manager.</summary>
public sealed class AgentConnection
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public AgentConnection(Guid agentId, WebSocket socket, string remoteAddress)
    {
        AgentId = agentId;
        _socket = socket;
        RemoteAddress = remoteAddress;
    }

    public Guid AgentId { get; }
    public string RemoteAddress { get; }
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public AgentHelloMessage? Hello { get; set; }
    public AgentSystemInfo? SystemInfo { get; set; }
    public DateTime LastMessageAt { get; set; } = DateTime.UtcNow;

    /// <summary>The version an update was last pushed for, so a reconnect doesn't re-push while it's in progress.</summary>
    public string? UpdateRequestedFor { get; set; }

    public bool IsOpen => _socket.State == WebSocketState.Open;

    internal WebSocket Socket => _socket;

    public async Task SendAsync(AgentMessage message, CancellationToken ct = default)
    {
        if (!IsOpen)
            throw new IOException("The agent is not connected.");
        var bytes = Encoding.UTF8.GetBytes(AgentProtocol.Serialize(message));
        await _sendLock.WaitAsync(ct);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (WebSocketException ex)
        {
            throw new IOException("Sending to the agent failed: " + ex.Message, ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseAsync(string reason)
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None);
        }
        catch { /* already gone */ }
    }
}

/// <summary>An entry in an agent's recent-activity list on its page.</summary>
public sealed record AgentActivity(DateTime At, string Level, string Message);
