using System.Threading.Channels;

using LR.Core.Agents;
using LR.Core.Wrapper;

namespace LR.Application.Agents;

/// <summary>
/// A wrapper transport relayed through an agent's WebSocket: sends become
/// <see cref="WrapperFrameMessage"/>s to the agent, and frames the agent forwards for this server
/// are queued for <see cref="ReceiveAsync"/>. Starts with the wrapper's hello already queued, like a
/// fresh pipe connection. Completes when the wrapper goes away or the agent disconnects.
/// </summary>
public sealed class RemoteWrapperTransport : IWrapperTransport
{
    private readonly Channel<WrapperMessage> _inbound = Channel.CreateUnbounded<WrapperMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly AgentConnection _connection;
    private readonly Action<RemoteWrapperTransport> _onDisposed;

    public RemoteWrapperTransport(AgentConnection connection, Guid instanceId, HelloEvent hello, Action<RemoteWrapperTransport> onDisposed)
    {
        _connection = connection;
        InstanceId = instanceId;
        _onDisposed = onDisposed;
        _inbound.Writer.TryWrite(hello);
    }

    public Guid InstanceId { get; }

    public AgentConnection Connection => _connection;

    public Task SendAsync(WrapperMessage message, CancellationToken ct = default)
    {
        if (_inbound.Reader.Completion.IsCompleted)
            throw new IOException("The agent connection for this server is closed.");
        return _connection.SendAsync(new WrapperFrameMessage { InstanceId = InstanceId, Frame = message }, ct);
    }

    public async Task<WrapperMessage?> ReceiveAsync(CancellationToken ct = default)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    internal void Deliver(WrapperMessage message) => _inbound.Writer.TryWrite(message);

    internal void Close() => _inbound.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        Close();
        _onDisposed(this);
        return ValueTask.CompletedTask;
    }
}
