using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Logs the full lifecycle of API requests passing through the router.
/// Captures incoming payloads, translated payloads, backend responses, and outgoing responses.
/// </summary>
public interface IApiRequestLogger
{
    /// <summary>
    /// Whether request logging is currently enabled (based on settings).
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Log an incoming API request. This is the first step in the lifecycle.
    /// </summary>
    Task<Guid> LogIncomingAsync(ApiProtocol protocol, string endpointPath, string incomingPayload, string? modelName);

    /// <summary>
    /// Log the translated payload that will be sent to the backend provider.
    /// </summary>
    Task LogTranslatedPayloadAsync(Guid logId, string translatedPayload);

    /// <summary>
    /// Log the raw response received from the backend.
    /// For streaming responses this is a summary unless LogFullPayloads is enabled.
    /// </summary>
    Task LogBackendResponseAsync(Guid logId, string? backendPayload);

    /// <summary>
    /// Log the outgoing response sent back to the client and mark as complete with metrics.
    /// </summary>
    Task LogCompletionAsync(
        Guid logId,
        ServerInstance? server,
        ModelPreset? preset,
        RouteResponse? routeResponse,
        int statusCode,
        string? outgoingSummary,
        bool isStreaming,
        bool wasQueued);

    /// <summary>
    /// Store the response ID (e.g. "chatcmpl-...") for future OpenAI response_id correlation.
    /// </summary>
    Task LogResponseIdAsync(Guid logId, string responseId);

    /// <summary>
    /// Log an error that occurred during request processing.
    /// </summary>
    Task LogErrorAsync(Guid logId, string errorMessage, int? statusCode = null);

    /// <summary>
    /// Get recent log entries for the overview page (most recent first).
    /// </summary>
    Task<(List<ApiRequestLog> Logs, long TotalCount)> GetRecentLogsAsync(
        int count,
        ApiProtocol? protocolFilter = null,
        DateTimeOffset? from = null);

    /// <summary>
    /// Get a single log entry by ID (for the detail page).
    /// </summary>
    Task<ApiRequestLog?> GetByIdAsync(Guid id);

    /// <summary>
    /// Delete all logs older than the given cutoff date.
    /// Returns the number of deleted entries.
    /// </summary>
    Task<long> DeleteOlderThanAsync(DateTimeOffset cutoff);

    /// <summary>
    /// Get summary statistics for the overview page cards.
    /// </summary>
    Task<(long TotalToday, long OpenAIToday, long ClaudeToday, long OllamaToday, double AvgLatencyMs)> GetSummaryStatsAsync();
}
