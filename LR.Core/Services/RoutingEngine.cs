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
            if (instance is null)
                continue;

            // Server matches the rule and is running/healthy — return it immediately
            // IsBusy is [NotMapped] so meaningless across requests; queue handles concurrency.
            if (instance.Status == ServerStatus.Running && instance.IsHealthy)
                return instance;

            // Server matches but is idle/errored — try to auto-start it
            if (await _serverManager.TryAutoStartAsync(rule.TargetServerInstanceId, cancellationToken))
            {
                var startedInstance = await GetInstanceAsync(rule.TargetServerInstanceId, cancellationToken);
                if (startedInstance?.Status == ServerStatus.Running && startedInstance.IsHealthy)
                    return startedInstance;
            }
        }

        // 2. Fallback: round-robin among healthy running servers
        // IsBusy is [NotMapped] so meaningless across requests; queue handles concurrency.
        var allInstances = _serverManager.GetAllInstances();
        var healthyInstances = allInstances
            .Where(s => s.Status == ServerStatus.Running && s.IsHealthy)
            .ToList();

        if (healthyInstances.Count > 0)
        {
            var instance = healthyInstances[_roundRobinIndex % healthyInstances.Count];
            _roundRobinIndex++;
            return instance;
        }

        // 3. No running server found — try to auto-start an idle one that matches the request by PresetId
        if (request.PresetId.HasValue)
        {
            var preset = await _context.ModelPresets.FindAsync(request.PresetId.Value);
            if (preset is not null)
            {
                var instance = await GetInstanceAsync(preset.ServerInstanceId, cancellationToken);
                // If the server is already running and healthy, return it directly
                if (instance is not null && instance.Status == ServerStatus.Running && instance.IsHealthy)
                    return instance;

                if (instance is not null && instance.Status != ServerStatus.Running)
                {
                    // Set this preset as active before starting
                    if (instance.ActivePresetId != request.PresetId.Value)
                    {
                        instance.ActivePresetId = request.PresetId.Value;
                        await _context.SaveChangesAsync();
                    }

                    if (await _serverManager.TryAutoStartAsync(preset.ServerInstanceId, cancellationToken))
                    {
                        var startedInstance = await GetInstanceAsync(preset.ServerInstanceId, cancellationToken);
                        if (startedInstance?.Status == ServerStatus.Running && startedInstance.IsHealthy)
                            return startedInstance;
                    }
                }
            }
        }

        // 4. Try by model name — find any server with a matching preset
        if (!string.IsNullOrWhiteSpace(request.ModelName))
        {
            var matchingPreset = await _context.ModelPresets
                .FirstOrDefaultAsync(p => p.Name == request.ModelName, cancellationToken);

            if (matchingPreset is not null)
            {
                var instance = await GetInstanceAsync(matchingPreset.ServerInstanceId, cancellationToken);
                // If the server is already running and healthy, return it directly
                if (instance is not null && instance.Status == ServerStatus.Running && instance.IsHealthy)
                    return instance;

                if (instance is not null && instance.Status != ServerStatus.Running)
                {
                    if (instance.ActivePresetId != matchingPreset.Id)
                    {
                        instance.ActivePresetId = matchingPreset.Id;
                        await _context.SaveChangesAsync();
                    }

                    if (await _serverManager.TryAutoStartAsync(matchingPreset.ServerInstanceId, cancellationToken))
                    {
                        var startedInstance = await GetInstanceAsync(matchingPreset.ServerInstanceId, cancellationToken);
                        if (startedInstance?.Status == ServerStatus.Running && startedInstance.IsHealthy)
                            return startedInstance;
                    }
                }
            }
        }

        // 5. Last resort: try to auto-start any idle server with a valid preset
        var idleInstances = allInstances
            .Where(s => s.Status == ServerStatus.Idle || s.Status == ServerStatus.Error)
            .ToList();

        foreach (var instance in idleInstances)
        {
            if (await _serverManager.TryAutoStartAsync(instance.Id, cancellationToken))
            {
                var startedInstance = await GetInstanceAsync(instance.Id, cancellationToken);
                if (startedInstance?.Status == ServerStatus.Running && startedInstance.IsHealthy)
                    return startedInstance;
            }
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
