namespace LR.Core.Models;

/// <summary>
/// A snapshot of a running llama.cpp server's live context (KV-cache) occupancy, derived from
/// its <c>/slots</c> endpoint. Refreshed roughly once a second by
/// <c>ServerLoadBroadcastService</c> and used for the dashboard context-usage gauge and the
/// optional context-aware queuing gate.
///
/// llama.cpp does not report a single "KV cache full" number — each parallel slot has its own
/// context window (<c>n_ctx</c>) and its own occupancy (prompt tokens already cached plus
/// tokens generated so far). This record therefore carries both a per-slot worst case
/// (<see cref="BusiestSlotUsageRatio"/> — the figure that predicts a context-exceeded error or
/// cache thrash) and a whole-server aggregate.
/// </summary>
public sealed record LlamaRuntimeUsage
{
    /// <summary>
    /// Highest <c>(prompt tokens + generated tokens) / n_ctx</c> across all slots (0–1). This is
    /// the number that matters for admission: a single slot approaching its window is what makes
    /// the next turn of that conversation overflow.
    /// </summary>
    public double? BusiestSlotUsageRatio { get; init; }

    /// <summary>
    /// Whole-server occupancy: total tokens held across every slot divided by the summed context
    /// windows (0–1). Lower than <see cref="BusiestSlotUsageRatio"/> whenever load is uneven.
    /// </summary>
    public double? AggregateUsageRatio { get; init; }

    /// <summary>Total tokens currently held in the KV cache, summed across all slots.</summary>
    public int? UsedTokens { get; init; }

    /// <summary>Summed context window across all slots (per-slot <c>n_ctx</c> × slot count).</summary>
    public int? ContextLimit { get; init; }

    /// <summary>Number of slots currently processing a request.</summary>
    public int? BusySlots { get; init; }

    /// <summary>Total number of slots the server exposes.</summary>
    public int? TotalSlots { get; init; }

    /// <summary>When this snapshot was read.</summary>
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}
