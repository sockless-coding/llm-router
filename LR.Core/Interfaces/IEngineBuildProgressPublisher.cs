using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Broadcasts engine-build (download/compile) progress to connected clients. Implemented in
/// LR.Application via SignalR — see <see cref="IModelDownloadProgressPublisher"/> for the
/// equivalent model-download publisher.
/// </summary>
public interface IEngineBuildProgressPublisher
{
    Task PublishAsync(EngineBuildProgress progress);
}
