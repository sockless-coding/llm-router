using LR.Core.Wrapper;

namespace LR.Core.Interfaces;

/// <summary>
/// Opens wrapper transports through connected remote agents — the remote counterpart of the named
/// pipe the router uses for its own machine's wrappers. Implemented in LR.Application by the
/// agent connection manager; consumed by the providers' process manager.
/// </summary>
public interface IRemoteWrapperConnector
{
    /// <summary>Whether <paramref name="agentId"/> currently has a live connection.</summary>
    bool IsAgentOnline(Guid agentId);

    /// <summary>
    /// Attaches to the wrapper of <paramref name="instanceId"/> on the agent, launching one when
    /// <paramref name="launch"/> is set. Returns null when the agent is offline or (not launching)
    /// no wrapper runs; throws when the agent reports a failure. The returned transport's first
    /// message is the wrapper's <see cref="HelloEvent"/>.
    /// </summary>
    Task<IWrapperTransport?> AttachAsync(Guid agentId, Guid instanceId, bool launch, CancellationToken ct);
}
