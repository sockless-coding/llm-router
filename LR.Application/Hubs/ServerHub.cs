using Microsoft.AspNetCore.SignalR;

using LR.Core.Models;

namespace LR.Application.Hubs;

/// <summary>
/// SignalR hub for pushing server lifecycle events to the UI in real-time.
/// </summary>
public class ServerHub : Hub
{
    /// <summary>
    /// Broadcast a startup progress event to all connected clients.
    /// </summary>
    public async Task SendStartupProgress(StartupProgressEvent @event)
    {
        await Clients.All.SendAsync("ReceiveStartupProgress", @event);
    }
}
