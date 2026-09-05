using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Records and queries model inference statistics from the database.
/// </summary>
public class StatisticsService : IStatisticsService
{
    private readonly LRDbContext _context;
    private readonly IStatHubPublisher _hubPublisher;

    public StatisticsService(LRDbContext context, IStatHubPublisher hubPublisher)
    {
        _context = context;
        _hubPublisher = hubPublisher;
    }

    /// <inheritdoc />
    public async Task RecordRequestAsync(ServerInstance server, ModelPreset? preset, RouteResponse response, Guid? apiKeyId = null)
    {
        var stat = new ModelStatistics
        {
            ServerInstanceId = server.Id,
            PresetId = preset?.Id,
            ApiKeyId = apiKeyId,
            Timestamp = DateTimeOffset.UtcNow,
            PromptTokensProcessed = response.PromptTokensProcessed,
            PromptProcessingMs = response.PromptProcessingMs,
            PromptTokensPerSecReported = response.PromptTokensPerSecond ?? 0,
            GeneratedTokenCount = response.GeneratedTokenCount,
            GenerationMs = response.GenerationMs,
            TotalLatencyMs = response.TotalLatencyMs,
            FirstTokenLatencyMs = response.FirstTokenLatencyMs,
            ContextLengthUsed = response.PromptTokensProcessed + response.GeneratedTokenCount,
            ContextMaxLength = preset?.ContextSize ?? 0,
            // Speculative decoding metrics (populated when speculative decoding is active)
            DraftAcceptanceRate = response.DraftAcceptanceRate,
            DraftAccepted = response.DraftAccepted,
            DraftGenerated = response.DraftGenerated,
            DraftMeanLen = response.DraftMeanLen,
        };

        _context.ModelStatistics.Add(stat);
        await _context.SaveChangesAsync();

        await _hubPublisher.PublishAsync(stat);
    }

    /// <inheritdoc />
    public async Task<List<ModelStatistics>> GetByServerAsync(Guid serverId, DateTimeOffset from, DateTimeOffset to)
    {
        return await _context.ModelStatistics
            .Where(s => s.ServerInstanceId == serverId && s.Timestamp >= from && s.Timestamp <= to)
            .OrderBy(s => s.Timestamp)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<Dictionary<Guid, double>> GetAvgPromptTokensPerSecByServerAsync(DateTimeOffset from, DateTimeOffset to)
    {
        var stats = await _context.ModelStatistics
            .Where(s => s.Timestamp >= from && s.Timestamp <= to
                && (s.PromptTokensPerSecReported > 0 || s.PromptProcessingMs > 0))
            .GroupBy(s => s.ServerInstanceId)
            .Select(g => new
            {
                ServerId = g.Key,
                AvgTokensPerSec = g.Average(s => s.PromptTokensPerSecReported > 0
                    ? s.PromptTokensPerSecReported
                    : (double)s.PromptTokensProcessed / s.PromptProcessingMs * 1000)
            })
            .ToListAsync();

        return stats.ToDictionary(x => x.ServerId, x => x.AvgTokensPerSec);
    }

    /// <inheritdoc />
    public async Task<Dictionary<Guid, double>> GetAvgGenTokensPerSecByServerAsync(DateTimeOffset from, DateTimeOffset to)
    {
        var stats = await _context.ModelStatistics
            .Where(s => s.Timestamp >= from && s.Timestamp <= to && s.GenerationMs > 0)
            .GroupBy(s => s.ServerInstanceId)
            .Select(g => new { ServerId = g.Key, AvgTokensPerSec = g.Average(s => (double)s.GeneratedTokenCount / s.GenerationMs * 1000) })
            .ToListAsync();

        return stats.ToDictionary(x => x.ServerId, x => x.AvgTokensPerSec);
    }

    /// <inheritdoc />
    public async Task<List<(DateTimeOffset Timestamp, int TokensUsed)>> GetContextUsageOverTimeAsync(Guid presetId, DateTimeOffset from, DateTimeOffset to)
    {
        var results = await _context.ModelStatistics
            .Where(s => s.PresetId == presetId && s.Timestamp >= from && s.Timestamp <= to)
            .OrderBy(s => s.Timestamp)
            .Select(s => new { s.Timestamp, TokensUsed = s.ContextLengthUsed })
            .ToListAsync();

        return results.Select(x => (x.Timestamp, x.TokensUsed)).ToList();
    }

    /// <inheritdoc />
    public async Task<long> GetTotalTokensProcessedAsync(Guid? serverId = null, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        IQueryable<ModelStatistics> query = _context.ModelStatistics;

        if (serverId.HasValue)
            query = query.Where(s => s.ServerInstanceId == serverId.Value);

        if (from.HasValue)
            query = query.Where(s => s.Timestamp >= from.Value);

        if (to.HasValue)
            query = query.Where(s => s.Timestamp <= to.Value);

        return await query.SumAsync(s => (long)s.PromptTokensProcessed + s.GeneratedTokenCount);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TokenBreakdownBucket>> GetTokenBreakdownOverTimeAsync(DateTimeOffset from, DateTimeOffset to, int buckets = 48)
    {
        if (to <= from) return Array.Empty<TokenBreakdownBucket>();
        if (buckets < 1) buckets = 1;

        // Pull just the three columns we need; bucketing is done in memory because the SQLite
        // provider stores Timestamp as an ISO-8601 string (see the value converter in
        // LRDbContext) and can't do arithmetic date bucketing in SQL.
        var rows = await _context.ModelStatistics
            .Where(s => s.Timestamp >= from && s.Timestamp <= to)
            .Select(s => new { s.Timestamp, s.PromptTokensProcessed, s.GeneratedTokenCount })
            .ToListAsync();

        var span = to - from;
        var bucketSize = TimeSpan.FromTicks(Math.Max(1, span.Ticks / buckets));

        var acc = new (long Prompt, long Gen)[buckets];
        foreach (var r in rows)
        {
            int idx = (int)((r.Timestamp - from).Ticks / bucketSize.Ticks);
            if (idx < 0) idx = 0;
            if (idx >= buckets) idx = buckets - 1;
            acc[idx].Prompt += r.PromptTokensProcessed;
            acc[idx].Gen += r.GeneratedTokenCount;
        }

        var result = new List<TokenBreakdownBucket>(buckets);
        for (int i = 0; i < buckets; i++)
            result.Add(new TokenBreakdownBucket(from + TimeSpan.FromTicks(bucketSize.Ticks * i), acc[i].Prompt, acc[i].Gen));

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApiKeyUsage>> GetUsageByApiKeyAsync(DateTimeOffset from, DateTimeOffset to)
    {
        var grouped = await _context.ModelStatistics
            .Where(s => s.Timestamp >= from && s.Timestamp <= to)
            .GroupBy(s => s.ApiKeyId)
            .Select(g => new
            {
                ApiKeyId = g.Key,
                RequestCount = (long)g.Count(),
                PromptTokens = g.Sum(s => (long)s.PromptTokensProcessed),
                GeneratedTokens = g.Sum(s => (long)s.GeneratedTokenCount),
                AvgLatencyMs = g.Average(s => s.TotalLatencyMs),
            })
            .ToListAsync();

        // Resolve key names/prefixes in one lookup rather than a join (keeps the query above
        // translatable and tolerates keys that have since been deleted → SetNull → null id).
        var keyIds = grouped.Where(x => x.ApiKeyId.HasValue).Select(x => x.ApiKeyId!.Value).ToList();
        var keys = await _context.ApiKeys
            .Where(k => keyIds.Contains(k.Id))
            .Select(k => new { k.Id, k.Name, k.KeyPrefix })
            .ToDictionaryAsync(k => k.Id);

        return grouped
            .Select(x =>
            {
                string name = "No key";
                string? prefix = null;
                if (x.ApiKeyId.HasValue && keys.TryGetValue(x.ApiKeyId.Value, out var k))
                {
                    name = k.Name;
                    prefix = k.KeyPrefix;
                }
                else if (x.ApiKeyId.HasValue)
                {
                    name = "Deleted key";
                }

                return new ApiKeyUsage(x.ApiKeyId, name, prefix, x.RequestCount, x.PromptTokens, x.GeneratedTokens, x.AvgLatencyMs);
            })
            .OrderByDescending(u => u.TotalTokens)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<long> GetTotalRequestCountAsync(Guid? serverId = null, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        IQueryable<ModelStatistics> query = _context.ModelStatistics;

        if (serverId.HasValue)
            query = query.Where(s => s.ServerInstanceId == serverId.Value);

        if (from.HasValue)
            query = query.Where(s => s.Timestamp >= from.Value);

        if (to.HasValue)
            query = query.Where(s => s.Timestamp <= to.Value);

        return await query.LongCountAsync();
    }

    /// <inheritdoc />
    public async Task<double> GetAvgTotalLatencyAsync(Guid? serverId = null, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        IQueryable<ModelStatistics> query = _context.ModelStatistics;

        if (serverId.HasValue)
            query = query.Where(s => s.ServerInstanceId == serverId.Value);

        if (from.HasValue)
            query = query.Where(s => s.Timestamp >= from.Value);

        if (to.HasValue)
            query = query.Where(s => s.Timestamp <= to.Value);

        // AverageAsync throws on empty sequence, so check first
        bool any = await query.AnyAsync();
        if (!any) return 0.0;

        return await query.AverageAsync(s => s.TotalLatencyMs);
    }
    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelPreset>> GetPresetsForContextUsageAsync(DateTimeOffset from, DateTimeOffset to)
    {
        // Select nullable first (EF Core can translate this), then filter nulls in memory
        var presetIds = await _context.ModelStatistics
            .Where(s => s.Timestamp >= from && s.Timestamp <= to)
            .Select(s => s.PresetId)
            .Distinct()
            .ToListAsync();

        var validPresetIds = presetIds.Where(id => id.HasValue).Select(id => id!.Value).ToList();

        if (validPresetIds.Count == 0) return new List<ModelPreset>();

        return await _context.ModelPresets
            .Where(p => validPresetIds.Contains(p.Id))
            .ToListAsync();
    }
}
