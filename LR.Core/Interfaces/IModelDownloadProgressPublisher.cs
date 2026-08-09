using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Broadcasts model download progress to connected clients. Implemented in LR.Application via
/// SignalR (see <see cref="ISignalRProgressPublisher"/> for the equivalent server-startup pattern).
/// </summary>
public interface IModelDownloadProgressPublisher
{
    Task PublishAsync(DownloadProgress progress);
}
