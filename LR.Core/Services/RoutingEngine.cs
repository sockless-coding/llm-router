using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Rule-based routing engine that evaluates incoming requests and selects the best server instance.
/// Rules are evaluated by priority (lowest number first). If no rule matches, falls back to round-robin.
/// Uses EF Core for rule persistence; runtime state (_roundRobinIndex) is kept in-memory.
/// </summary>
public class RoutingEngine : IRoutingEngine
{
    private readonly LRDbContext _context;
    private readonly IServerManager _serverManager;
    private int _roundRobinIndex;

    public RoutingEngine(LRDbContext context, IServerManager serverManager)
    {
        _context = context;
        _serverManager = serverManager;
    }

    public async Task<ServerInstance?> RouteAsync(RouteRequest request, CancellationToken cancellationToken = default)
    {
        // 1. Evaluate rules by priority from database
        var rules = await _context.RoutingRules.OrderBy(r => r.Priority).ToListAsync();

        foreach (var rule in rules)
        {
            if (!Matches(rule, request))
                continue;

            var instance = await GetInstanceAsync(rule.TargetServerInstanceId, cancellationToken);
            if (instance is not null && instance.Status == ServerStatus.Running && instance.IsHealthy && !instance.IsBusy)
                return instance;
        }

        // 2. Fallback: round-robin among healthy running and available (not busy) servers
        var allInstances = _serverManager.GetAllInstances();
        var healthyInstances = allInstances
            .Where(s => s.Status == ServerStatus.Running && s.IsHealthy && !s.IsBusy)
            .ToList();

        if (healthyInstances.Count > 0)
        {
            var instance = healthyInstances[_roundRobinIndex % healthyInstances.Count];
            _roundRobinIndex++;
            return instance;
        }

        return null;
    }

    public async Task AddRuleAsync(RoutingRule rule)
    {
        _context.RoutingRules.Add(rule);
        await _context.SaveChangesAsync();
    }

    public void AddRule(RoutingRule rule)
    {
        _context.RoutingRules.Add(rule);
        _context.SaveChanges();
    }

    public async Task<bool> RemoveRuleAsync(Guid ruleId)
    {
        var rule = await _context.RoutingRules.FindAsync(ruleId);
        if (rule is null) return false;

        _context.RoutingRules.Remove(rule);
        await _context.SaveChangesAsync();
        return true;
    }

    public bool RemoveRule(Guid ruleId)
    {
        var rule = _context.RoutingRules.Find(ruleId);
        if (rule is null) return false;

        _context.RoutingRules.Remove(rule);
        _context.SaveChanges();
        return true;
    }

    public async Task<IReadOnlyList<RoutingRule>> GetRulesAsync()
    {
        var rules = await _context.RoutingRules.OrderBy(r => r.Priority).ToListAsync();
        return rules.AsReadOnly();
    }

    public IReadOnlyList<RoutingRule> GetRules()
    {
        return _context.RoutingRules.OrderBy(r => r.Priority).ToList().AsReadOnly();
    }

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
