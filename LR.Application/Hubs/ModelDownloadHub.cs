using Microsoft.AspNetCore.SignalR;

using LR.Core.Models;

namespace LR.Application.Hubs;

/// <summary>
/// SignalR hub for pushing model download progress to the UI in real-time (see
/// <see cref="ServerHub"/> for the equivalent server-startup-progress hub).
/// </summary>
public class ModelDownloadHub : Hub
{
    public async Task SendDownloadProgress(DownloadProgress progress)
    {
        await Clients.All.SendAsync("ReceiveDownloadProgress", progress);
    }
}
