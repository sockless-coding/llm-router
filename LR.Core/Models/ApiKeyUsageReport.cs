namespace LR.Core.Models;

/// <summary>The bucket size of a usage report.</summary>
public enum UsagePeriod
{
    Day,
    /// <summary>ISO weeks, starting Monday.</summary>
    Week,
    Month,
    Year,
    /// <summary>One bucket spanning the whole requested range.</summary>
    Total,
}

/// <summary>
/// What a usage report covers: the local calendar dates <see cref="From"/> through
/// <see cref="To"/> (both inclusive) in <see cref="TimeZone"/>, bucketed by <see cref="Period"/>.
/// </summary>
/// <param name="ApiKeyId">Restrict to one key; <see cref="Guid.Empty"/> selects the "no key" bucket. Null = all keys.</param>
/// <param name="ModelName">Restrict to one model name. Null = all models.</param>
public record UsageReportQuery(
    UsagePeriod Period,
    DateOnly From,
    DateOnly To,
    TimeZoneInfo TimeZone,
    Guid? ApiKeyId = null,
    string? ModelName = null);

/// <summary>
/// Usage of one model by one API key within one report period.
/// </summary>
/// <param name="PeriodStart">First local date of the period (for <see cref="UsagePeriod.Total"/>, the report's start date).</param>
/// <param name="ApiKeyId"><see cref="Guid.Empty"/> for requests that carried no key.</param>
/// <param name="ApiKeyName">The key's current name, or its last known name if it was deleted.</param>
/// <param name="KeyDeleted">True when the key no longer exists.</param>
public record ApiKeyUsageReportRow(
    DateOnly PeriodStart,
    Guid ApiKeyId,
    string ApiKeyName,
    string? ApiKeyPrefix,
    bool KeyDeleted,
    string ModelName,
    long RequestCount,
    long PromptTokens,
    long GeneratedTokens)
{
    public long TotalTokens => PromptTokens + GeneratedTokens;
}
