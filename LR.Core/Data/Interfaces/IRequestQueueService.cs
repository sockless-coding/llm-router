using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Queues incoming inference requests when all servers are busy.
/// Dispatches to available servers as they become free.
/// </summary>
public interface IRequestQueueService
{
    /// <summary>
    /// Enqueue a request and wait for the response. Blocks until a server processes it or timeout occurs.
    /// </summary>
    Task<RouteResponse> EnqueueAsync(RouteRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Signal that a server has become available so queued requests can be dispatched.
    /// </summary>
    void ServerAvailable(Guid serverId);

    /// <summary>
    /// Try to dequeue the oldest pending request that can be served by a server whose active
    /// preset is <paramref name="activePresetId"/>. A request with no resolved preset (unknown
    /// model) matches any server. Requests that don't match are left in the queue in their
    /// original relative order. Returns true if a request was dequeued.
    /// </summary>
    bool TryDequeueMatching(Guid? activePresetId, out (RouteRequest Request, TaskCompletionSource<RouteResponse> Tcs) item);

    /// <summary>
    /// Check if there are any pending requests in the queue.
    /// </summary>
    bool HasPendingRequests { get; }
}
