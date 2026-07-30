using System.Collections.Concurrent;
using System.Threading.Channels;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Queues incoming inference requests when all servers are busy.
/// Uses Channel<T> for thread-safe async queuing with backpressure.
/// This is a singleton that holds only the channel + settings;
/// actual dispatch runs inside scoped services to access DbContext.
/// </summary>
public class RequestQueueService : IRequestQueueService, IDisposable
{
    private readonly GatewaySettings _settings;

    /// <summary>
    /// Queue of pending requests awaiting a free server.
    /// Each item is (request, completion source that resolves when the request finishes).
    /// </summary>
    private Channel<(RouteRequest Request, TaskCompletionSource<RouteResponse> Tcs)>? _channel;

    public RequestQueueService(GatewaySettings settings)
    {
        _settings = settings;

        int capacity = _settings.MaxQueueSize > 0 ? _settings.MaxQueueSize : int.MaxValue;
        _channel = Channel.CreateBounded<(RouteRequest, TaskCompletionSource<RouteResponse>)>(capacity);
    }

    public async Task<RouteResponse> EnqueueAsync(RouteRequest request, CancellationToken cancellationToken)
    {
        if (_channel == null) throw new ObjectDisposedException(nameof(RequestQueueService));

        var tcs = new TaskCompletionSource<RouteResponse>();

        try
        {
            if (_settings.QueueTimeoutSeconds > 0)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_settings.QueueTimeoutSeconds));

                await _channel.Writer.WriteAsync((request, tcs), timeoutCts.Token);
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            else
            {
                await _channel.Writer.WriteAsync((request, tcs), cancellationToken);
                return await tcs.Task;
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Request timed out while waiting in the queue.");
        }
    }

    /// <summary>
    /// Try to dequeue a pending request. Returns true if a request was dequeued.
    /// </summary>
    public bool TryDequeue(out (RouteRequest Request, TaskCompletionSource<RouteResponse> Tcs) item)
    {
        item = default!;
        return _channel?.Reader.TryRead(out item!) == true;
    }

    /// <summary>
    /// Check if there are pending items in the queue.
    /// </summary>
    public bool HasPendingRequests => _channel?.Reader.Count > 0;

    /// <summary>
    /// Signal that a server has become available so queued requests can be dispatched.
    /// The dispatcher background service polls this; we just need to ensure the channel is ready.
    /// </summary>
    public void ServerAvailable(Guid serverId)
    {
        // The RequestDispatcherService polls periodically — no explicit signal needed here
        // beyond ensuring the channel writer is open. Future: could use a SemaphoreSlim or
        // similar to wake the dispatcher immediately instead of polling.
    }

    public void Dispose()
    {
        _channel?.Writer.Complete();
    }
}
