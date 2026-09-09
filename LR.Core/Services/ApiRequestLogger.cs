using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Logs the full lifecycle of API requests passing through the router.
/// When logging is disabled (via GatewaySettings.EnableRequestLogging), all operations are no-ops.
/// </summary>
public class ApiRequestLogger : IApiRequestLogger
{
    private readonly LRDbContext _context;
    private readonly ILogger<ApiRequestLogger> _logger;
    private readonly GatewaySettings _settings;
    private readonly IApiKeyRequestContext _apiKeyContext;

    public bool IsEnabled => _settings.EnableRequestLogging;

    public ApiRequestLogger(
        LRDbContext context,
        ILogger<ApiRequestLogger> logger,
        IOptions<GatewaySettings> settings,
        IApiKeyRequestContext apiKeyContext)
    {
        _context = context;
        _logger = logger;
        _settings = settings.Value;
        _apiKeyContext = apiKeyContext;
    }

    /// <inheritdoc />
    public async Task<Guid> LogIncomingAsync(ApiProtocol protocol, string endpointPath, string incomingPayload, string? modelName)
    {
        if (!_settings.EnableRequestLogging) return Guid.Empty;

        var log = new ApiRequestLog
        {
            Protocol = protocol,
            EndpointPath = endpointPath,
            IncomingPayload = TruncateForStorage(incomingPayload) ?? string.Empty,
            ModelName = modelName,
            // Auth runs (as an endpoint filter) before the handler calls this, so the key — if
            // any — is already resolved on the shared request scope.
            ApiKeyId = _apiKeyContext.CurrentKey?.Id
        };

        _context.ApiRequestLogs.Add(log);
        await _context.SaveChangesAsync();
        return log.Id;
    }

    /// <inheritdoc />
    public async Task LogTranslatedPayloadAsync(Guid logId, string translatedPayload)
    {
        if (!_settings.EnableRequestLogging || logId == Guid.Empty) return;

        var log = await _context.ApiRequestLogs.FindAsync(logId);
        if (log is null) return;

        log.TranslatedPayload = TruncateForStorage(translatedPayload);
        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task LogBackendResponseAsync(Guid logId, string? backendPayload)
    {
        if (!_settings.EnableRequestLogging || logId == Guid.Empty) return;

        var log = await _context.ApiRequestLogs.FindAsync(logId);
        if (log is null) return;

        // When LogFullPayloads is disabled, we store a summary for backend responses too
        log.BackendResponsePayload = backendPayload is not null ? TruncateForStorage(backendPayload) : null;
        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task LogCompletionAsync(
        Guid logId,
        ServerInstance? server,
        ModelPreset? preset,
        RouteResponse? routeResponse,
        int statusCode,
        string? outgoingSummary,
        bool isStreaming,
        bool wasQueued)
    {
        if (!_settings.EnableRequestLogging || logId == Guid.Empty) return;

        var log = await _context.ApiRequestLogs.FindAsync(logId);
        if (log is null) return;

        log.ServerInstanceId = server?.Id;
        log.PresetId = preset?.Id;
        log.StatusCode = statusCode;
        log.IsStreaming = isStreaming;
        log.WasQueued = wasQueued;

        if (routeResponse is not null)
        {
            log.TotalLatencyMs = routeResponse.TotalLatencyMs;
            log.FirstTokenLatencyMs = routeResponse.FirstTokenLatencyMs;
            log.PromptTokensProcessed = routeResponse.PromptTokensProcessed;
            log.GeneratedTokenCount = routeResponse.GeneratedTokenCount;
        }

        if (outgoingSummary is not null)
        {
            log.OutgoingPayloadSummary = TruncateForStorage(outgoingSummary);
        }

        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task LogResponseIdAsync(Guid logId, string responseId)
    {
        if (!_settings.EnableRequestLogging || logId == Guid.Empty) return;

        var log = await _context.ApiRequestLogs.FindAsync(logId);
        if (log is null) return;

        log.ResponseId = responseId;
        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task LogErrorAsync(Guid logId, string errorMessage, int? statusCode = null)
    {
        if (!_settings.EnableRequestLogging || logId == Guid.Empty) return;

        var log = await _context.ApiRequestLogs.FindAsync(logId);
        if (log is null) return;

        log.ErrorMessage = errorMessage.Length > 4096 ? errorMessage[..4096] : errorMessage;
        if (statusCode.HasValue)
            log.StatusCode = statusCode.Value;

        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<(List<ApiRequestLog> Logs, long TotalCount)> GetRecentLogsAsync(
        int count,
        ApiProtocol? protocolFilter = null,
        DateTimeOffset? from = null)
    {
        var query = _context.ApiRequestLogs.AsNoTracking();

        if (protocolFilter.HasValue)
            query = query.Where(l => l.Protocol == protocolFilter.Value);

        if (from.HasValue)
            query = query.Where(l => l.Timestamp >= from.Value);

        // Timestamp is stored as round-trip UTC TEXT (see LRDbContext), so the filter,
        // ordering and Take all translate to SQL and ride IX_ApiRequestLogs_Timestamp —
        // only `count` rows are materialised. The payload chain (TEXT columns up to 1 MB
        // each) is projected away so SQLite never has to walk their overflow pages.
        long totalCount = await query.LongCountAsync();

        var logs = await query
            .OrderByDescending(l => l.Timestamp)
            .Take(count)
            .Select(l => new ApiRequestLog
            {
                Id = l.Id,
                Timestamp = l.Timestamp,
                Protocol = l.Protocol,
                EndpointPath = l.EndpointPath,
                ModelName = l.ModelName,
                ServerInstance = l.ServerInstance == null ? null : new ServerInstance { Name = l.ServerInstance.Name },
                TotalLatencyMs = l.TotalLatencyMs,
                FirstTokenLatencyMs = l.FirstTokenLatencyMs,
                PromptTokensProcessed = l.PromptTokensProcessed,
                GeneratedTokenCount = l.GeneratedTokenCount,
                StatusCode = l.StatusCode,
                IsStreaming = l.IsStreaming,
                WasQueued = l.WasQueued,
                ErrorMessage = l.ErrorMessage,
            })
            .ToListAsync();

        return (logs, totalCount);
    }

    /// <inheritdoc />
    public async Task<ApiRequestLog?> GetByIdAsync(Guid id)
    {
        return await _context.ApiRequestLogs.FindAsync(id);
    }

    /// <inheritdoc />
    public async Task<long> DeleteOlderThanAsync(DateTimeOffset cutoff)
    {
        // Timestamp is round-trip UTC TEXT (see LRDbContext), so the cutoff comparison
        // translates to a single indexed DELETE.
        int deleted = await _context.ApiRequestLogs
            .Where(l => l.Timestamp < cutoff)
            .ExecuteDeleteAsync();

        if (deleted > 0)
            _logger.LogInformation("Deleted {Count} request logs older than {Cutoff}", deleted, cutoff);
        return deleted;
    }

    /// <inheritdoc />
    public async Task<(long TotalToday, long OpenAIToday, long ClaudeToday, long OllamaToday, double AvgLatencyMs)> GetSummaryStatsAsync()
    {
        var startOfDay = new DateTimeOffset(DateTimeOffset.UtcNow.Date, TimeSpan.Zero);

        // Timestamp is round-trip UTC TEXT (see LRDbContext), so the day-window filter
        // rides IX_ApiRequestLogs_Timestamp and only today's rows are aggregated — grouped
        // by protocol in a single round trip.
        var today = _context.ApiRequestLogs
            .AsNoTracking()
            .Where(l => l.Timestamp >= startOfDay);

        var byProtocol = await today
            .GroupBy(l => l.Protocol)
            .Select(g => new { Protocol = g.Key, Count = g.Count() })
            .ToListAsync();

        long CountFor(ApiProtocol p) => byProtocol.FirstOrDefault(x => x.Protocol == p)?.Count ?? 0;

        long totalToday = byProtocol.Sum(x => (long)x.Count);
        // Nullable selector → AverageAsync yields null (not a throw) when nothing matched.
        double avgLatencyMs = await today.AverageAsync(l => (double?)l.TotalLatencyMs) ?? 0;

        return (totalToday, CountFor(ApiProtocol.OpenAI), CountFor(ApiProtocol.Claude), CountFor(ApiProtocol.Ollama), avgLatencyMs);
    }

    /// <summary>
    /// Truncates payload strings if LogFullPayloads is disabled.
    /// When full payloads are enabled, returns the string as-is.
    /// </summary>
    private string? TruncateForStorage(string? value)
    {
        if (value is null) return null;

        // Always allow at least this much for debugging even when truncated
        const int maxTruncatedLength = 8192;   // 8KB for partial payloads
        const int maxFullLength = 1_048_576;   // 1MB for full payloads — large system prompts/tool schemas (e.g. Copilot's) can run tens of KB on their own

        if (_settings.LogFullPayloads)
            return value.Length > maxFullLength ? value[..maxFullLength] : value;

        return value.Length > maxTruncatedLength ? value[..maxTruncatedLength] : value;
    }
}
