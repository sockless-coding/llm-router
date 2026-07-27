namespace LR.Core.Models;

/// <summary>
/// The current operational status of a server instance.
/// </summary>
public enum ServerStatus
{
    Idle = 0,
    Running = 1,
    Stopping = 2,
    Error = 3,
}
