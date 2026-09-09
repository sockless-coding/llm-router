using System.Text.Json;

using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Turns a llama.cpp <c>/slots</c> response into a <see cref="LlamaRuntimeUsage"/> snapshot.
/// Kept separate from the provider so the aggregation is unit-testable without an HTTP round trip.
/// </summary>
public static class LlamaSlotsSnapshot
{
    /// <summary>
    /// Parses a <c>/slots</c> response body. Accepts both a bare JSON array and the
    /// <c>{ "slots": [...] }</c> wrapper some builds use. Returns null when the body isn't a
    /// recognisable slots payload.
    /// </summary>
    public static LlamaRuntimeUsage? FromJson(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            return FromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Aggregates the slots element. Per slot, the tokens held is <c>n_prompt_tokens</c> (the
    /// cached prompt) plus <c>n_decoded</c> from the first <c>next_token</c> entry (tokens
    /// generated so far this turn); slots that have never run report zero.
    /// </summary>
    public static LlamaRuntimeUsage? FromElement(JsonElement root)
    {
        JsonElement slots;
        if (root.ValueKind == JsonValueKind.Array)
            slots = root;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("slots", out var s) && s.ValueKind == JsonValueKind.Array)
            slots = s;
        else
            return null;

        int totalSlots = 0, busySlots = 0;
        long usedSum = 0, ctxSum = 0;
        double busiestRatio = 0;

        foreach (var slot in slots.EnumerateArray())
        {
            totalSlots++;

            int nCtx = slot.TryGetProperty("n_ctx", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
            if (slot.TryGetProperty("is_processing", out var p) && p.ValueKind == JsonValueKind.True)
                busySlots++;

            int promptTokens = slot.TryGetProperty("n_prompt_tokens", out var pt) && pt.TryGetInt32(out var ptv) && ptv > 0 ? ptv : 0;
            int decoded = 0;
            if (slot.TryGetProperty("next_token", out var nt) && nt.ValueKind == JsonValueKind.Array && nt.GetArrayLength() > 0
                && nt[0].TryGetProperty("n_decoded", out var nd) && nd.TryGetInt32(out var ndv) && ndv > 0)
                decoded = ndv;

            int used = promptTokens + decoded;
            usedSum += used;
            ctxSum += nCtx;

            if (nCtx > 0)
                busiestRatio = Math.Max(busiestRatio, (double)used / nCtx);
        }

        return new LlamaRuntimeUsage
        {
            BusiestSlotUsageRatio = ctxSum > 0 ? busiestRatio : null,
            AggregateUsageRatio = ctxSum > 0 ? (double)usedSum / ctxSum : null,
            UsedTokens = (int)Math.Min(usedSum, int.MaxValue),
            ContextLimit = (int)Math.Min(ctxSum, int.MaxValue),
            BusySlots = busySlots,
            TotalSlots = totalSlots,
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }
}
