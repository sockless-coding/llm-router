namespace LR.Core.Wrapper;

/// <summary>
/// A bidirectional channel to one wrapper — either the local named pipe
/// (<see cref="WrapperPipeConnection"/>) or a relay through a remote agent. The router's process
/// manager only talks to this, so local and remote servers share one lifecycle implementation.
/// The first message received on a fresh transport is always the wrapper's <see cref="HelloEvent"/>.
/// </summary>
public interface IWrapperTransport : IAsyncDisposable
{
    /// <summary>Sends one message. Safe to call concurrently.</summary>
    Task SendAsync(WrapperMessage message, CancellationToken ct = default);

    /// <summary>Reads the next message; null once the transport is closed.</summary>
    Task<WrapperMessage?> ReceiveAsync(CancellationToken ct = default);
}
