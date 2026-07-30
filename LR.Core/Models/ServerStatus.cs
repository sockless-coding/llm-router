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
}
