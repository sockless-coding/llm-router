using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Optional capability implemented by backend providers that can report how many inference
/// requests the running server will process concurrently (its resolved slot count) and other
/// runtime facts read from the engine. Not part of <see cref="IBackendProvider"/> itself since
/// not every engine exposes this. The routing engine uses <see cref="MaxConcurrentRequests"/>
/// to cap how many requests it passes through to a given server at once.
/// </summary>
public interface IServerCapacityProvider
{
    /// <summary>
    /// Maximum number of concurrent inference requests the running server accepts, or null if
    /// it is not yet known (server still starting) or not applicable to this engine.
    /// </summary>
    int? MaxConcurrentRequests { get; }

    /// <summary>
    /// The last snapshot read from the running server's <c>/props</c> endpoint, or null if it
    /// has not been read yet (server still starting) or the engine has no such endpoint.
    /// </summary>
    LlamaServerProps? ServerProps { get; }

    /// <summary>
    /// The last snapshot of live context (KV-cache) occupancy read from the running server's
    /// <c>/slots</c> endpoint, or null if it has not been read yet or the engine exposes no such
    /// endpoint. Updated by <see cref="RefreshRuntimeUsageAsync"/>.
    /// </summary>
    LlamaRuntimeUsage? RuntimeUsage { get; }

    /// <summary>
    /// Best-effort refresh of <see cref="RuntimeUsage"/> from the running server. Any failure is
    /// swallowed and the previous snapshot is left in place. Called on a short cadence by the
    /// live server-load broadcast.
    /// </summary>
    Task RefreshRuntimeUsageAsync(CancellationToken cancellationToken = default);
}
