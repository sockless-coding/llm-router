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
        // Resolve which preset (i.e. which model) this request actually wants, up front.
        // Every path below uses this so we always route to — and (re)start if necessary —
        // the one server instance that owns the requested model, instead of silently handing
        // the request to whatever instance happens to already be running a different model.
        var targetPreset = await ResolveTargetPresetAsync(request, cancellationToken);

        // 1. Evaluate rules by priority from database (admin-defined overrides)
        var rules = await _context.RoutingRules.OrderBy(r => r.Priority).ToListAsync(cancellationToken);

        foreach (var rule in rules)
        {
            if (!Matches(rule, request))
                continue;

            var instance = await GetInstanceAsync(rule.TargetServerInstanceId, cancellationToken);
            if (instance is null)
                continue;

            var ready = await EnsureInstanceServesPresetAsync(instance, targetPreset, cancellationToken);
            if (ready is not null)
                return ready;
        }

        // 2. Known model — route straight to (and start/restart as needed) the server instance
        // that owns the matching preset, whether it's idle, errored, or currently running a
        // different model.
        if (targetPreset is not null)
        {
            var instance = await GetInstanceAsync(targetPreset.ServerInstanceId, cancellationToken);
            if (instance is not null)
            {
                var ready = await EnsureInstanceServesPresetAsync(instance, targetPreset, cancellationToken);
                if (ready is not null)
                    return ready;
            }
        }

        // 3. Fallback: round-robin among healthy running servers already serving the requested
        // model (or any healthy server if we couldn't resolve which model was requested).
        // IsBusy is [NotMapped] so meaningless across requests; queue handles concurrency.
        var allInstances = _serverManager.GetAllInstances();
        var healthyInstances = allInstances
            .Where(s => s.Status == ServerStatus.Running && s.IsHealthy)
            .Where(s => targetPreset is null || s.ActivePresetId == targetPreset.Id)
            .ToList();

        if (healthyInstances.Count > 0)
        {
            var instance = healthyInstances[_roundRobinIndex % healthyInstances.Count];
            _roundRobinIndex++;
            return instance;
        }

        // 4. Still nothing, and we don't even know which model was requested — try starting
        // any idle server with a valid preset so *something* can serve the request. (When a
        // model WAS resolved, step 2 above already tried its designated instance — starting an
        // unrelated idle server here would just serve the wrong model.)
        if (targetPreset is null)
        {
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
        }

        return null;
    }

    /// <summary>
    /// Resolves the ModelPreset a request targets, preferring an explicit PresetId and
    /// falling back to a case-sensitive model name lookup. Returns null if the request
    /// doesn't map to any known preset.
    /// </summary>
    private async Task<ModelPreset?> ResolveTargetPresetAsync(RouteRequest request, CancellationToken cancellationToken)
    {
        if (request.PresetId.HasValue)
        {
            var preset = await _context.ModelPresets.FindAsync([request.PresetId.Value], cancellationToken);
            if (preset is not null)
                return preset;
        }

        if (!string.IsNullOrWhiteSpace(request.ModelName))
            return await _context.ModelPresets.FirstOrDefaultAsync(p => p.Name == request.ModelName, cancellationToken);

        return null;
    }

    /// <summary>
    /// Ensures the given instance is running the requested preset's model, (re)starting it if
    /// needed. Returns the instance immediately if it's already running the correct model and
    /// healthy. Otherwise kicks off a start/restart in the background (model loading can take a
    /// while) and returns null so the caller queues the request until the instance comes up.
    /// </summary>
    private async Task<ServerInstance?> EnsureInstanceServesPresetAsync(ServerInstance instance, ModelPreset? targetPreset, CancellationToken cancellationToken)
    {
        // No specific model resolved for this request — any running, healthy instance will do.
        if (targetPreset is null)
        {
            if (instance.Status == ServerStatus.Running && instance.IsHealthy)
                return instance;

            if (instance.Status == ServerStatus.Idle || instance.Status == ServerStatus.Error)
            {
                if (await _serverManager.TryAutoStartAsync(instance.Id, cancellationToken))
                {
                    var started = await GetInstanceAsync(instance.Id, cancellationToken);
                    if (started?.Status == ServerStatus.Running && started.IsHealthy)
                        return started;
                }
            }

            return null;
        }

        // Already running the requested model — use it directly.
        if (instance.Status == ServerStatus.Running && instance.IsHealthy && instance.ActivePresetId == targetPreset.Id)
            return instance;

        // Running, but a DIFFERENT model is loaded — stop it and restart with the correct preset.
        if (instance.Status == ServerStatus.Running && instance.ActivePresetId != targetPreset.Id)
        {
            await _serverManager.RestartWithPresetAsync(instance.Id, targetPreset.Id, cancellationToken);
            return null; // restart runs in the background — caller should queue/retry
        }

        // Idle/errored — activate the requested preset and start it.
        if (instance.Status == ServerStatus.Idle || instance.Status == ServerStatus.Error)
        {
            if (instance.ActivePresetId != targetPreset.Id)
            {
                instance.ActivePresetId = targetPreset.Id;
                await _context.SaveChangesAsync(cancellationToken);
            }

            await _serverManager.TryAutoStartAsync(instance.Id, cancellationToken);
        }

        // Starting/Stopping (already in flight, possibly from the branches above) — wait it out.
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
