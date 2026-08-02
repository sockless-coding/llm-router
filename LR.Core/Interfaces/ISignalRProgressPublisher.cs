using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Publishes startup progress events to SignalR clients.
/// Abstraction to avoid circular dependency between LR.Core and LR.Application.
/// </summary>
public interface ISignalRProgressPublisher
{
    /// <summary>
    /// Broadcast a startup progress event to all connected clients.
    /// </summary>
    Task PublishAsync(StartupProgressEvent @event);
}
