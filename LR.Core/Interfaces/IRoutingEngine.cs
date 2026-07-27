using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Evaluates incoming requests and selects the best server instance to route to.
/// </summary>
public interface IRoutingEngine
{
    /// <summary>
    /// Routes an incoming request to a target server instance based on configured rules.
    /// Returns null if no suitable server is found.
    /// </summary>
    Task<ServerInstance?> RouteAsync(RouteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a routing rule. Rules are evaluated by priority (lowest first).
    /// </summary>
    void AddRule(RoutingRule rule);

    /// <summary>
    /// Removes a routing rule by ID.
    /// </summary>
    bool RemoveRule(Guid ruleId);

    /// <summary>
    /// Gets all configured routing rules ordered by priority.
    /// </summary>
    IReadOnlyList<RoutingRule> GetRules();
}
