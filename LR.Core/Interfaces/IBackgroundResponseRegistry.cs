namespace LR.Core.Interfaces;

/// <summary>
/// Tracks cancellation tokens for in-flight background (async) Responses API requests, keyed by
/// response id, so POST /v1/responses/{id}/cancel can abort them. Registered as a singleton —
/// background processing tasks outlive the HTTP request scope that started them.
/// </summary>
public interface IBackgroundResponseRegistry
{
    /// <summary>Registers the cancellation source for a newly started background response.</summary>
    void Register(string responseId, CancellationTokenSource cts);

    /// <summary>
    /// Requests cancellation of an in-flight background response. Returns false if no such
    /// response is currently registered (already completed, or not a background response).
    /// </summary>
    bool TryCancel(string responseId);

    /// <summary>
    /// Removes the registration once the background task has reached a terminal state.
    /// A cancel racing with this is harmless — TryCancel simply returns false afterward.
    /// </summary>
    void Remove(string responseId);
}
