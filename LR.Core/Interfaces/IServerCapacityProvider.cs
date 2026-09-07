namespace LR.Core.Interfaces;

/// <summary>
/// Optional capability implemented by backend providers that can report how many inference
/// requests the running server will process concurrently (its resolved slot count). Not part
/// of <see cref="IBackendProvider"/> itself since not every engine exposes this. The routing
/// engine uses it to cap how many requests it passes through to a given server at once.
/// </summary>
public interface IServerCapacityProvider
{
    /// <summary>
    /// Maximum number of concurrent inference requests the running server accepts, or null if
    /// it is not yet known (server still starting) or not applicable to this engine.
    /// </summary>
    int? MaxConcurrentRequests { get; }
}
