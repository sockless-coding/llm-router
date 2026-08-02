using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Service for automatically restarting crashed server instances with configurable retry limits.
/// </summary>
public interface IAutoRestartService
{
    /// <summary>
    /// Attempts to restart a server instance if it hasn't exceeded the maximum restart count.
    /// Returns true if the restart was attempted, false if max retries were reached or already restarting.
    /// </summary>
    Task<bool> AttemptRestartAsync(ServerInstance instance);

    /// <summary>
    /// Resets the restart counter for a server (called after successful manual start).
    /// </summary>
    void ResetRestartCount(int serverInstanceId);
}
