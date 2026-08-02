namespace LR.Core.Models;

/// <summary>
/// Types of progress events emitted during server startup.
/// </summary>
public enum StartupEventType
{
    /// <summary>Server is being initialized, status set to Starting.</summary>
    Starting,

    /// <summary>The server process has been launched (model loading in progress).</summary>
    ProcessStarted,

    /// <summary>Polling for health check — server not yet healthy. Message includes elapsed time.</summary>
    HealthChecking,

    /// <summary>Server is now healthy and running.</summary>
    Healthy,

    /// <summary>Startup failed with an error.</summary>
    Error
}
