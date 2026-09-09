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

    /// <summary>
    /// Whether a new request for <paramref name="provider"/>'s server should be held in the queue
    /// because the server's context is (almost) full. True only when
    /// <see cref="GatewaySettings.ContextAwareQueuing"/> is enabled, the server already has at
    /// least one request in flight (<paramref name="inFlight"/> ≥ 1 — a server with nothing
    /// running is always admitted, so a queued request can never be starved indefinitely), and
    /// its busiest slot's context usage is at or above
    /// <see cref="GatewaySettings.ContextUsageQueueThresholdPercent"/>. The busiest slot (not the
    /// whole-server average) is the right measure: a continuing conversation is routed back to
    /// the slot holding its prefix, so it is that one slot filling up that overflows the next
    /// turn. Returns false for providers that don't report runtime usage (non-llama.cpp engines,
    /// or a server whose <c>/slots</c> has not been read yet).
    /// </summary>
    public static bool ShouldHoldForContext(IBackendProvider? provider, GatewaySettings settings, int inFlight)
    {
        if (!settings.ContextAwareQueuing || inFlight < 1)
            return false;

        if ((provider as IServerCapacityProvider)?.RuntimeUsage?.BusiestSlotUsageRatio is not double ratio)
            return false;

        return ratio * 100.0 >= settings.ContextUsageQueueThresholdPercent;
    }
}
