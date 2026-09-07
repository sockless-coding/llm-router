using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Resolves how many requests may be in flight against a llama.cpp server at once, from the
/// best source available: the server's own reported slot count (<c>/props</c> <c>total_slots</c>,
/// the resolved <c>-np</c>), else the preset's <see cref="ModelPreset.Parallel"/> when positive,
/// else the configured default. Shared by the routing engine, the queue dispatcher, and the
/// live slot-usage broadcast so they all agree on capacity.
/// </summary>
public static class LlamaSlotCapacity
{
    public static int Resolve(IBackendProvider? provider, ModelPreset? preset, int defaultSlots)
    {
        if ((provider as IServerCapacityProvider)?.MaxConcurrentRequests is int reported && reported > 0)
            return reported;

        if (preset?.Parallel is int p && p > 0)
            return p;

        return defaultSlots > 0 ? defaultSlots : 1;
    }
}
