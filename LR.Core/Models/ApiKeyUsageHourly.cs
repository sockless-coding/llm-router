using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// Hourly usage rollup per API key and model — the durable source for usage reports. One row
/// accumulates every request a key made against a model within one UTC hour, so day / week /
/// month / year reports in any whole-hour-offset timezone are just sums over these rows.
/// <para>
/// Unlike <see cref="ApiRequestLog"/> (purged by the retention policy) these rows are never
/// cleaned up — they're tiny (keys × models × 24 a day at most). The key and model names are
/// snapshotted rather than foreign-keyed so reports still name a key or preset that was later
/// deleted.
/// </para>
/// </summary>
[Table("ApiKeyUsageHourly")]
public class ApiKeyUsageHourly
{
    /// <summary>Start of the UTC hour this row covers (minutes/seconds are always zero).</summary>
    public DateTimeOffset HourStart { get; set; }

    /// <summary>
    /// The API key the requests authenticated with, or <see cref="Guid.Empty"/> for requests that
    /// carried no key (auth disabled). Not a foreign key — the row outlives a deleted key.
    /// </summary>
    public Guid ApiKeyId { get; set; }

    /// <summary>The preset (model) name the requests were served by, as named at the time.</summary>
    [Required, MaxLength(128)]
    public string ModelName { get; set; } = string.Empty;

    /// <summary>The key's name at the time of the most recent request in this hour.</summary>
    [Required, MaxLength(128)]
    public string ApiKeyName { get; set; } = string.Empty;

    /// <summary>The key's display prefix at the time of the most recent request in this hour.</summary>
    [MaxLength(24)]
    public string? ApiKeyPrefix { get; set; }

    /// <summary>The preset that served the requests, if known. Not a foreign key.</summary>
    public Guid? PresetId { get; set; }

    public long RequestCount { get; set; }

    public long PromptTokens { get; set; }

    public long GeneratedTokens { get; set; }
}
