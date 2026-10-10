using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Keeps the servers of a <see cref="MemoryGroup"/> within its device-memory budget: before a
/// server loads a preset, least-recently-used servers in the same group that have no requests in
/// flight are stopped until the estimated footprints (<see cref="PresetMemoryEstimator"/>) fit.
/// Servers that are starting, busy, or running a <see cref="ModelPreset.KeepLoaded"/> preset are
/// never evicted; if the model still doesn't fit after
/// every eligible eviction, the start goes ahead anyway (the estimate may be pessimistic) and
/// the shortfall is reported so it can be logged.
/// </summary>
public class MemoryGroupScheduler
{
    /// <summary>
    /// Serializes make-room decisions process-wide, so two servers starting at once can't both
    /// count the same free memory. Held by the caller until the starting server is marked
    /// <see cref="ServerStatus.Starting"/> — from then on it counts as loaded for the next one.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly LRDbContext _context;
    private readonly IServerConcurrencyLimiter _limiter;
    private readonly Func<string, long?> _fileSize;

    public MemoryGroupScheduler(LRDbContext context, IServerConcurrencyLimiter limiter, Func<string, long?>? fileSize = null)
    {
        _context = context;
        _limiter = limiter;
        _fileSize = fileSize ?? PresetMemoryEstimator.FileSizeOnDisk;
    }

    public sealed record Result(IReadOnlyList<ServerInstance> Evicted, long NeededBytes, long UsedBytes, long BudgetBytes, IDisposable Lock) : IDisposable
    {
        public bool Fits => BudgetBytes <= 0 || UsedBytes + NeededBytes <= BudgetBytes;
        public void Dispose() => Lock.Dispose();
    }

    /// <summary>
    /// Makes room for <paramref name="starting"/> to load <paramref name="preset"/>, calling
    /// <paramref name="stop"/> for each evicted server. The caller must dispose the result once
    /// the starting server's status is persisted. Servers with no group (or a group with no
    /// budget) return immediately with nothing evicted.
    /// </summary>
    public async Task<Result> MakeRoomAsync(
        ServerInstance starting, ModelPreset preset, Func<Guid, CancellationToken, Task> stop, CancellationToken cancellationToken)
    {
        var none = new Result([], 0, 0, 0, NoopLock.Instance);
        if (string.IsNullOrWhiteSpace(starting.MemoryGroup))
            return none;

        var group = await _context.MemoryGroups.FindAsync([starting.MemoryGroup], cancellationToken);
        if (group is not { BudgetMb: > 0 })
            return none;

        await Gate.WaitAsync(cancellationToken);
        var releaser = new GateLock();
        try
        {
            long budget = group.BudgetMb * 1024L * 1024L;
            long needed = await EstimateAsync(preset, cancellationToken);

            // Everything else in the group that holds (or is about to hold) device memory. The
            // starting server itself is excluded: a model swap frees its current footprint.
            var loaded = await _context.ServerInstances
                .Where(s => s.MemoryGroup == starting.MemoryGroup && s.Id != starting.Id)
                .Where(s => s.Status == ServerStatus.Running || s.Status == ServerStatus.Starting || s.Status == ServerStatus.Reconnecting)
                .ToListAsync(cancellationToken);

            var footprints = new Dictionary<Guid, long>();
            var pinned = new HashSet<Guid>();
            foreach (var server in loaded)
            {
                var active = server.ActivePresetId is Guid id ? await _context.ModelPresets.FindAsync([id], cancellationToken) : null;
                footprints[server.Id] = active is null ? 0 : await EstimateAsync(active, cancellationToken);
                if (active is { KeepLoaded: true })
                    pinned.Add(server.Id);
            }

            long used = footprints.Values.Sum();
            var evicted = new List<ServerInstance>();

            var candidates = loaded
                .Where(s => s.Status == ServerStatus.Running && _limiter.InFlight(s.Id) == 0 && !pinned.Contains(s.Id))
                .OrderBy(s => _limiter.LastActivity(s.Id) ?? DateTimeOffset.MinValue)
                .ToList();

            foreach (var victim in candidates)
            {
                if (used + needed <= budget)
                    break;

                try
                {
                    await stop(victim.Id, cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    continue; // couldn't stop it — its memory stays counted; try the next one
                }
                used -= footprints[victim.Id];
                evicted.Add(victim);
            }

            return new Result(evicted, needed, used, budget, releaser);
        }
        catch
        {
            releaser.Dispose();
            throw;
        }
    }

    public async Task<long> EstimateAsync(ModelPreset preset, CancellationToken cancellationToken = default)
    {
        var model = preset.ModelId is Guid id ? await _context.LocalModels.FindAsync([id], cancellationToken) : null;
        return PresetMemoryEstimator.EstimateBytes(preset, model, _fileSize);
    }

    private sealed class GateLock : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                Gate.Release();
        }
    }

    private sealed class NoopLock : IDisposable
    {
        public static readonly NoopLock Instance = new();
        public void Dispose() { }
    }
}
