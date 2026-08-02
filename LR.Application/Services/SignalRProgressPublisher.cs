using Microsoft.AspNetCore.SignalR;

using LR.Application.Hubs;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Services;

/// <summary>
/// Publishes startup progress events to SignalR clients via IHubContext.
/// </summary>
public class SignalRProgressPublisher : ISignalRProgressPublisher
{
    private readonly IHubContext<ServerHub> _hubContext;

    public SignalRProgressPublisher(IHubContext<Hubs.ServerHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task PublishAsync(StartupProgressEvent @event)
    {
        await _hubContext.Clients.All.SendAsync(
            "ReceiveStartupProgress",
            @event.InstanceId,
            (int)@event.EventType,
            @event.Message,
            @event.ElapsedSeconds);
    }
}
