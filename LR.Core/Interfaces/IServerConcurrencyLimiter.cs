namespace LR.Core.Interfaces;

/// <summary>
/// Tracks how many inference requests the router currently has in flight against each server
/// instance and enforces a per-server ceiling that matches the server's slot capacity
/// (llama.cpp's <c>--parallel</c>/<c>-np</c>). Requests that cannot reserve a slot are queued
/// by the caller until one frees up.
///
/// Singleton: in-flight counts must be shared across every request scope and background service.
/// </summary>
public interface IServerConcurrencyLimiter
{
    /// <summary>
    /// Attempts to reserve one in-flight slot on <paramref name="serverId"/>. Returns a lease
    /// that must be disposed exactly when the request finishes (success, failure, or cancel),
    /// or null if the server already has <paramref name="capacity"/> requests in flight.
    /// </summary>
    IDisposable? TryAcquire(Guid serverId, int capacity);

    /// <summary>
    /// Current number of in-flight requests reserved against <paramref name="serverId"/>.
    /// </summary>
    int InFlight(Guid serverId);
}
