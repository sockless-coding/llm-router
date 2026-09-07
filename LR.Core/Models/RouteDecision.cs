namespace LR.Core.Models;

/// <summary>
/// The result of <see cref="LR.Core.Interfaces.IRoutingEngine.RouteAsync"/> when a server is
/// available: the target instance plus the concurrency lease reserved for this request. The
/// caller must dispose it (via <c>using</c>) once the request finishes so the slot is returned
/// to the pool.
/// </summary>
public sealed class RouteDecision : IDisposable
{
    /// <summary>The server instance to send the request to.</summary>
    public required ServerInstance Server { get; init; }

    /// <summary>
    /// The reserved concurrency slot. Disposed when the request completes, fails, or is
    /// cancelled — including the full duration of a streamed response.
    /// </summary>
    public required IDisposable Lease { get; init; }

    public void Dispose() => Lease.Dispose();
}
