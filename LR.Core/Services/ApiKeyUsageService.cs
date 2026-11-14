using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Keeps the <see cref="ApiKeyUsageHourly"/> rollup current and builds usage reports from it.
/// </summary>
public class ApiKeyUsageService : IApiKeyUsageService
{
    /// <summary>Display name for requests that carried no API key.</summary>
    public const string NoKeyName = "No key";

    /// <summary>Model name recorded when the serving preset couldn't be resolved.</summary>
    public const string UnknownModelName = "(unknown)";

    private readonly LRDbContext _context;

    public ApiKeyUsageService(LRDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task RecordAsync(Guid? apiKeyId, string modelName, Guid? presetId, long promptTokens, long generatedTokens, DateTimeOffset? at = null)
    {
        var keyId = apiKeyId ?? Guid.Empty;
        string keyName = NoKeyName;
        string? keyPrefix = null;
        if (keyId != Guid.Empty)
        {
            var key = await _context.ApiKeys.AsNoTracking()
                .Where(k => k.Id == keyId)
                .Select(k => new { k.Name, k.KeyPrefix })
                .FirstOrDefaultAsync();
            keyName = key?.Name ?? "Deleted key";
            keyPrefix = key?.KeyPrefix;
        }

        var model = string.IsNullOrWhiteSpace(modelName) ? UnknownModelName : modelName.Trim();
        if (model.Length > 128) model = model[..128];

        var hour = HourStartUtc(at ?? DateTimeOffset.UtcNow);

        // An atomic upsert rather than read-modify-write: concurrent requests from the same key
        // in the same hour would otherwise race to insert the same primary key. HourStart is
        // passed pre-formatted because the "O" value converter (see LRDbContext) only applies to
        // LINQ comparisons against the property, not to raw SQL parameters.
        await _context.Database.ExecuteSqlAsync($"""
            INSERT INTO ApiKeyUsageHourly
                (HourStart, ApiKeyId, ModelName, ApiKeyName, ApiKeyPrefix, PresetId, RequestCount, PromptTokens, GeneratedTokens)
            VALUES
                ({FormatHour(hour)}, {keyId}, {model}, {keyName}, {keyPrefix}, {presetId}, 1, {Math.Max(0, promptTokens)}, {Math.Max(0, generatedTokens)})
            ON CONFLICT (HourStart, ApiKeyId, ModelName) DO UPDATE SET
                RequestCount = RequestCount + 1,
                PromptTokens = PromptTokens + excluded.PromptTokens,
                GeneratedTokens = GeneratedTokens + excluded.GeneratedTokens,
                ApiKeyName = excluded.ApiKeyName,
                ApiKeyPrefix = excluded.ApiKeyPrefix,
                PresetId = COALESCE(excluded.PresetId, PresetId);
            """);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApiKeyUsageReportRow>> GetReportAsync(UsageReportQuery query)
    {
        var (fromDate, toDate) = query.From <= query.To ? (query.From, query.To) : (query.To, query.From);
        var fromUtc = LocalMidnightToUtc(fromDate, query.TimeZone);
        var toUtc = LocalMidnightToUtc(toDate.AddDays(1), query.TimeZone);

        IQueryable<ApiKeyUsageHourly> rows = _context.ApiKeyUsageHourly.AsNoTracking()
            .Where(u => u.HourStart >= fromUtc && u.HourStart < toUtc);
        if (query.ApiKeyId is { } keyId)
            rows = rows.Where(u => u.ApiKeyId == keyId);
        if (!string.IsNullOrEmpty(query.ModelName))
            rows = rows.Where(u => u.ModelName == query.ModelName);

        // Bucketing happens in memory: periods are local-calendar based (DST, month lengths),
        // which SQLite can't do on the TEXT timestamps — and the rollup is small enough not to matter.
        var hourly = await rows.ToListAsync();

        var currentKeys = await _context.ApiKeys.AsNoTracking()
            .Select(k => new { k.Id, k.Name, k.KeyPrefix })
            .ToDictionaryAsync(k => k.Id);

        return hourly
            .GroupBy(u => (
                Period: PeriodStart(LocalDate(u.HourStart, query.TimeZone), query.Period, fromDate),
                u.ApiKeyId,
                u.ModelName))
            .Select(g =>
            {
                var latest = g.MaxBy(u => u.HourStart)!;
                string name;
                string? prefix;
                bool deleted = false;
                if (g.Key.ApiKeyId == Guid.Empty)
                {
                    name = NoKeyName;
                    prefix = null;
                }
                else if (currentKeys.TryGetValue(g.Key.ApiKeyId, out var key))
                {
                    name = key.Name;
                    prefix = key.KeyPrefix;
                }
                else
                {
                    name = latest.ApiKeyName;
                    prefix = latest.ApiKeyPrefix;
                    deleted = true;
                }

                return new ApiKeyUsageReportRow(
                    g.Key.Period, g.Key.ApiKeyId, name, prefix, deleted, g.Key.ModelName,
                    g.Sum(u => u.RequestCount), g.Sum(u => u.PromptTokens), g.Sum(u => u.GeneratedTokens));
            })
            .OrderBy(r => r.PeriodStart)
            .ThenByDescending(r => r.TotalTokens)
            .ThenBy(r => r.ApiKeyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ModelName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The start of the UTC hour containing <paramref name="at"/>.</summary>
    public static DateTimeOffset HourStartUtc(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Matches the round-trip UTC TEXT the DateTimeOffset converter in LRDbContext writes.</summary>
    public static string FormatHour(DateTimeOffset hour) => hour.UtcDateTime.ToString("O");

    /// <summary>The first local date of the period that <paramref name="date"/> falls in.</summary>
    public static DateOnly PeriodStart(DateOnly date, UsagePeriod period, DateOnly rangeStart) => period switch
    {
        UsagePeriod.Day => date,
        // ISO weeks start Monday: DayOfWeek.Sunday (0) is 6 days after Monday.
        UsagePeriod.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        UsagePeriod.Month => new DateOnly(date.Year, date.Month, 1),
        UsagePeriod.Year => new DateOnly(date.Year, 1, 1),
        UsagePeriod.Total => rangeStart,
        _ => date,
    };

    private static DateOnly LocalDate(DateTimeOffset utc, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, tz).DateTime);

    /// <summary>
    /// The UTC instant at which local date <paramref name="date"/> begins in <paramref name="tz"/>.
    /// Where DST skips midnight, the day begins at the first valid local time after it.
    /// </summary>
    public static DateTimeOffset LocalMidnightToUtc(DateOnly date, TimeZoneInfo tz)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        for (int i = 0; i < 8 && tz.IsInvalidTime(local); i++)
            local = local.AddMinutes(30);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, tz), TimeSpan.Zero);
    }
}
