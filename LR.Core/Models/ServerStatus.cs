using System.ComponentModel.DataAnnotations;

namespace LR.Core.Models;

/// <summary>
/// The current operational status of a server instance.
/// </summary>
public enum ServerStatus
{
    Idle = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Error = 4,

    /// <summary>
    /// Transient status set while the router's boot-time reconciliation pass is
    /// re-attaching to a wrapper process that outlived a previous router process.
    /// </summary>
    Reconnecting = 5,
}
