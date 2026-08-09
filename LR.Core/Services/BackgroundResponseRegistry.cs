using System.Collections.Concurrent;

using LR.Core.Interfaces;

namespace LR.Core.Services;

/// <inheritdoc cref="IBackgroundResponseRegistry"/>
public class BackgroundResponseRegistry : IBackgroundResponseRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new();

    public void Register(string responseId, CancellationTokenSource cts) => _inFlight[responseId] = cts;

    public bool TryCancel(string responseId)
    {
        if (!_inFlight.TryGetValue(responseId, out var cts)) return false;
        cts.Cancel();
        return true;
    }

    public void Remove(string responseId) => _inFlight.TryRemove(responseId, out _);
}
