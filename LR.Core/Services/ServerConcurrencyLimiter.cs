using System.Collections.Concurrent;

using LR.Core.Interfaces;

namespace LR.Core.Services;

/// <summary>
/// Default <see cref="IServerConcurrencyLimiter"/>: a per-server in-flight counter guarded by a
/// single lock. Contention is negligible — the counts are only touched once when a request is
/// admitted and once when it completes, and the router only ever has a handful of requests in
/// flight per server.
/// </summary>
public sealed class ServerConcurrencyLimiter : IServerConcurrencyLimiter
{
    /// <summary>
    /// A lease that reserves nothing — handed out for servers the limiter does not gate
    /// (non-llama.cpp engines) so callers can treat every route decision uniformly.
    /// </summary>
    public static readonly IDisposable NoopLease = new NoopDisposable();

    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _inFlight = new();

    public IDisposable? TryAcquire(Guid serverId, int capacity)
    {
        if (capacity <= 0)
            capacity = 1;

        lock (_gate)
        {
            _inFlight.TryGetValue(serverId, out var current);
            if (current >= capacity)
                return null;

            _inFlight[serverId] = current + 1;
        }

        return new Lease(this, serverId);
    }

    public int InFlight(Guid serverId)
    {
        lock (_gate)
        {
            _inFlight.TryGetValue(serverId, out var current);
            return current;
        }
    }

    private void Release(Guid serverId)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(serverId, out var current))
                return;

            if (current <= 1)
                _inFlight.Remove(serverId);
            else
                _inFlight[serverId] = current - 1;
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly ServerConcurrencyLimiter _owner;
        private readonly Guid _serverId;
        private int _released;

        public Lease(ServerConcurrencyLimiter owner, Guid serverId)
        {
            _owner = owner;
            _serverId = serverId;
        }

        public void Dispose()
        {
            // Idempotent — a route decision may be disposed via `using` and again in a finally.
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _owner.Release(_serverId);
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
