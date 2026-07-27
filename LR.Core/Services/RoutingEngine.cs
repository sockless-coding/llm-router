using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Rule-based routing engine that evaluates incoming requests and selects the best server instance.
/// Rules are evaluated by priority (lowest number first). If no rule matches, falls back to round-robin.
/// </summary>
public class RoutingEngine : IRoutingEngine
{
    private readonly IServerManager _serverManager;
    private readonly List<RoutingRule> _rules = new();
    private int _roundRobinIndex;

    public RoutingEngine(IServerManager serverManager)
    {
        _serverManager = serverManager;
    }

    public async Task<ServerInstance?> RouteAsync(RouteRequest request, CancellationToken cancellationToken = default)
    {
        // 1. Evaluate rules by priority
        foreach (var rule in _rules.OrderBy(r => r.Priority))
        {
            if (!Matches(rule, request))
                continue;

            var instance = await GetInstanceAsync(rule.TargetServerInstanceId, cancellationToken);
            if (instance is not null && instance.Status == ServerStatus.Running && instance.IsHealthy)
                return instance;
        }

        // 2. Fallback: round-robin among healthy running servers
        var healthyInstances = _serverManager.GetAllInstances()
            .Where(s => s.Status == ServerStatus.Running && s.IsHealthy)
            .ToList();

        if (healthyInstances.Count > 0)
        {
            var instance = healthyInstances[_roundRobinIndex % healthyInstances.Count];
            _roundRobinIndex++;
            return instance;
        }

        return null;
    }

    public void AddRule(RoutingRule rule) => _rules.Add(rule);

    public bool RemoveRule(Guid ruleId)
    {
        var index = _rules.FindIndex(r => r.Id == ruleId);
        if (index < 0)
            return false;
        _rules.RemoveAt(index);
        return true;
    }

    public IReadOnlyList<RoutingRule> GetRules() => _rules.AsReadOnly();

    private bool Matches(RoutingRule rule, RouteRequest request)
    {
        if (rule.ModelName is not null && !string.Equals(rule.ModelName, request.ModelName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (rule.PresetId.HasValue && rule.PresetId.Value != request.PresetId)
            return false;

        if (rule.BackendType.HasValue && rule.BackendType.Value != request.PreferredBackend)
            return false;

        return true;
    }

    private Task<ServerInstance?> GetInstanceAsync(Guid instanceId, CancellationToken ct = default)
    {
        var health = _serverManager.GetHealthAsync(instanceId);
        return health;
    }
}
