using Microsoft.AspNetCore.SignalR;

using LR.Application.Hubs;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Services;

/// <summary>
/// Publishes model download progress events to SignalR clients via IHubContext (see
/// <see cref="SignalRProgressPublisher"/> for the equivalent server-startup-progress publisher).
/// </summary>
public class ModelDownloadProgressPublisher : IModelDownloadProgressPublisher
{
    private readonly IHubContext<ModelDownloadHub> _hubContext;

    public ModelDownloadProgressPublisher(IHubContext<ModelDownloadHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task PublishAsync(DownloadProgress progress)
    {
        await _hubContext.Clients.All.SendAsync("ReceiveDownloadProgress", progress);
    }
}
