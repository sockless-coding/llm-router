using Microsoft.AspNetCore.SignalR;

using LR.Core.Models;

namespace LR.Application.Hubs;

/// <summary>
/// SignalR hub for pushing engine-build (download/compile) progress to the UI in real-time (see
/// <see cref="ModelDownloadHub"/> for the equivalent model-download hub).
/// </summary>
public class EngineBuildHub : Hub
{
    public async Task SendBuildProgress(EngineBuildProgress progress)
    {
        await Clients.All.SendAsync("ReceiveBuildProgress", progress);
    }
}
