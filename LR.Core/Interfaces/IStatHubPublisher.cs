using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Publishes newly recorded inference statistics to SignalR clients.
/// Abstraction to avoid circular dependency between LR.Core and LR.Application
/// (see <see cref="ISignalRProgressPublisher"/> for the equivalent server-startup pattern).
/// </summary>
public interface IStatHubPublisher
{
    /// <summary>
    /// Broadcast a newly recorded statistics entry to all connected clients.
    /// </summary>
    Task PublishAsync(ModelStatistics stat);
}
