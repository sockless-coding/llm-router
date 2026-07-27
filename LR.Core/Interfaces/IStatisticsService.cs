using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Service for recording and querying model inference statistics.
/// </summary>
public interface IStatisticsService
{
    /// <summary>
    /// Records a single completed inference request as a statistics entry.
    /// </summary>
    Task RecordRequestAsync(ServerInstance server, ModelPreset? preset, RouteResponse response);

    /// <summary>
    /// Gets all statistics for a given server within the specified time range.
    /// </summary>
    Task<List<ModelStatistics>> GetByServerAsync(Guid serverId, DateTimeOffset from, DateTimeOffset to);

    /// <summary>
    /// Gets average prompt processing throughput (tokens/sec) per server over the time range.
    /// Returns a dictionary of ServerInstanceId → avg tokens/sec.
    /// </summary>
    Task<Dictionary<Guid, double>> GetAvgPromptTokensPerSecByServerAsync(DateTimeOffset from, DateTimeOffset to);

    /// <summary>
    /// Gets average generation throughput (tokens/sec) per server over the time range.
    /// Returns a dictionary of ServerInstanceId → avg tokens/sec.
    /// </summary>
    Task<Dictionary<Guid, double>> GetAvgGenTokensPerSecByServerAsync(DateTimeOffset from, DateTimeOffset to);

    /// <summary>
    /// Gets context window usage over time for a given preset.
    /// Returns a list of (timestamp, tokens used) tuples.
    /// </summary>
    Task<List<(DateTimeOffset Timestamp, int TokensUsed)>> GetContextUsageOverTimeAsync(Guid presetId, DateTimeOffset from, DateTimeOffset to);

    /// <summary>
    /// Gets total tokens processed (prompt + generation) across all or a specific server.
    /// Optionally scoped by time range.
    /// </summary>
    Task<long> GetTotalTokensProcessedAsync(Guid? serverId = null, DateTimeOffset? from = null, DateTimeOffset? to = null);

    /// <summary>
    /// Gets total request count across all or a specific server.
    /// Optionally scoped by time range.
    /// </summary>
    Task<long> GetTotalRequestCountAsync(Guid? serverId = null, DateTimeOffset? from = null, DateTimeOffset? to = null);

    /// <summary>
    /// Gets average total latency in milliseconds across all or a specific server.
    /// Optionally scoped by time range.
    /// </summary>
    Task<double> GetAvgTotalLatencyAsync(Guid? serverId = null, DateTimeOffset? from = null, DateTimeOffset? to = null);

    /// <summary>
    /// Gets distinct presets that have statistics entries in the given time range.
    /// Used for context usage chart selection.
    /// </summary>
    Task<IReadOnlyList<ModelPreset>> GetPresetsForContextUsageAsync(DateTimeOffset from, DateTimeOffset to);
}
