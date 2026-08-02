using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Service for logging and retrieving server log entries from the database.
/// </summary>
public interface IServerLogService
{
    /// <summary>
    /// Logs a message for the given server instance at the specified level.
    /// </summary>
    Task LogAsync(ServerInstance instance, ServerLogLevel level, string message);

    /// <summary>
    /// Gets the most recent log entries for a server instance (newest first).
    /// </summary>
    Task<List<ServerLog>> GetLogsAsync(Guid serverInstanceId, int count = 100);

    /// <summary>
    /// Clears all log entries for a server instance.
    /// </summary>
    Task ClearLogsAsync(Guid serverInstanceId);
}

public enum ServerLogLevel
{
    Info,
    Warning,
    Error
}
