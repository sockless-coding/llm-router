namespace LR.Core.Models;

/// <summary>
/// One time bucket of input vs output token totals, summed across every server, for the
/// "Token Usage" dashboard chart.
/// </summary>
public record TokenBreakdownBucket(DateTimeOffset BucketStart, long PromptTokens, long GeneratedTokens);

/// <summary>
/// Aggregated inference usage for a single API key (or the synthetic "no key" bucket) over a
/// time range, for the per-key usage table on the statistics dashboard.
/// </summary>
public record ApiKeyUsage(
    Guid? ApiKeyId,
    string Name,
    string? KeyPrefix,
    long RequestCount,
    long PromptTokens,
    long GeneratedTokens,
    double AvgLatencyMs)
{
    public long TotalTokens => PromptTokens + GeneratedTokens;
}
