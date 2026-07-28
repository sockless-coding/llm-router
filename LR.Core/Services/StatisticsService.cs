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

    public StatisticsService(LRDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task RecordRequestAsync(ServerInstance server, ModelPreset? preset, RouteResponse response)
    {
        var stat = new ModelStatistics
        {
            ServerInstanceId = server.Id,
            PresetId = preset?.Id,
            Timestamp = DateTimeOffset.UtcNow,
            PromptTokensProcessed = response.PromptTokensProcessed,
            PromptProcessingMs = response.PromptProcessingMs,
            GeneratedTokenCount = response.GeneratedTokenCount,
            GenerationMs = response.GenerationMs,
            TotalLatencyMs = response.TotalLatencyMs,
            FirstTokenLatencyMs = response.FirstTokenLatencyMs,
            ContextLengthUsed = response.PromptTokensProcessed + response.GeneratedTokenCount,
            ContextMaxLength = preset?.ContextSize ?? 0,
        };

        _context.ModelStatistics.Add(stat);
        await _context.SaveChangesAsync();
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
            .Where(s => s.Timestamp >= from && s.Timestamp <= to && s.PromptProcessingMs > 0)
            .GroupBy(s => s.ServerInstanceId)
            .Select(g => new { ServerId = g.Key, AvgTokensPerSec = g.Average(s => (double)s.PromptTokensProcessed / s.PromptProcessingMs * 1000) })
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

        double? avg = await query.AverageAsync(s => s.TotalLatencyMs) as double?;
        return avg.HasValue ? avg.Value : 0.0;
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
