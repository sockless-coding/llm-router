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

        // The time-window filter and ordering run client-side because the SQLite EF
        // provider can't translate DateTimeOffset comparisons. To keep that affordable we
        // first project away the payload chain — those TEXT columns run to ~1 MB each and
        // the list view never shows them — and pull only the columns the table renders.
        var rows = await query
            .Select(l => new
            {
                l.Id,
                l.Timestamp,
                l.Protocol,
                l.EndpointPath,
                l.ModelName,
                ServerName = l.ServerInstance != null ? l.ServerInstance.Name : null,
                l.TotalLatencyMs,
                l.FirstTokenLatencyMs,
                l.PromptTokensProcessed,
                l.GeneratedTokenCount,
                l.StatusCode,
                l.IsStreaming,
                l.WasQueued,
                l.ErrorMessage,
            })
            .ToListAsync();

        var window = from.HasValue
            ? rows.Where(r => r.Timestamp >= from.Value).ToList()
            : rows;

        long totalCount = window.Count;
        var logs = window
            .OrderByDescending(r => r.Timestamp)
            .Take(count)
            .Select(r => new ApiRequestLog
            {
                Id = r.Id,
                Timestamp = r.Timestamp,
                Protocol = r.Protocol,
                EndpointPath = r.EndpointPath,
                ModelName = r.ModelName,
                ServerInstance = r.ServerName is null ? null : new ServerInstance { Name = r.ServerName },
                TotalLatencyMs = r.TotalLatencyMs,
                FirstTokenLatencyMs = r.FirstTokenLatencyMs,
                PromptTokensProcessed = r.PromptTokensProcessed,
                GeneratedTokenCount = r.GeneratedTokenCount,
                StatusCode = r.StatusCode,
                IsStreaming = r.IsStreaming,
                WasQueued = r.WasQueued,
                ErrorMessage = r.ErrorMessage,
            })
            .ToList();

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
        // SQLite EF provider can't translate DateTimeOffset comparisons in SQL, so the
        // cutoff filter runs client-side — but scan only (Id, Timestamp) rather than
        // pulling every row's payload columns just to decide what to delete.
        var stamps = await _context.ApiRequestLogs
            .AsNoTracking()
            .Select(l => new { l.Id, l.Timestamp })
            .ToListAsync();
        var idsToDelete = stamps.Where(l => l.Timestamp < cutoff).Select(l => l.Id).ToList();

        if (idsToDelete.Count == 0) return 0;

        // Guid IN (...) translates fine; chunk so the parameter list never gets huge.
        foreach (var chunk in idsToDelete.Chunk(500))
            await _context.ApiRequestLogs.Where(l => chunk.Contains(l.Id)).ExecuteDeleteAsync();

        _logger.LogInformation("Deleted {Count} request logs older than {Cutoff}", idsToDelete.Count, cutoff);
        return idsToDelete.Count;
    }

    /// <inheritdoc />
    public async Task<(long TotalToday, long OpenAIToday, long ClaudeToday, long OllamaToday, double AvgLatencyMs)> GetSummaryStatsAsync()
    {
        var startOfDay = DateTimeOffset.UtcNow.Date;

        // Client-side filter for DateTimeOffset — SQLite can't translate it — but project
        // to just the three fields the cards need rather than loading whole entities
        // (payload columns included) for the entire table.
        var allLogs = await _context.ApiRequestLogs
            .AsNoTracking()
            .Select(l => new { l.Timestamp, l.Protocol, l.TotalLatencyMs })
            .ToListAsync();
        var todayLogs = allLogs.Where(l => l.Timestamp >= startOfDay).ToList();

        long totalToday = (long)todayLogs.Count;
        long openaiToday = todayLogs.Count(l => l.Protocol == ApiProtocol.OpenAI);
        long claudeToday = todayLogs.Count(l => l.Protocol == ApiProtocol.Claude);
        long ollamaToday = todayLogs.Count(l => l.Protocol == ApiProtocol.Ollama);
        double avgLatencyMs = todayLogs.Average(l => (double?)l.TotalLatencyMs) ?? 0;

        return (totalToday, openaiToday, claudeToday, ollamaToday, avgLatencyMs);
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
