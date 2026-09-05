using Microsoft.AspNetCore.SignalR;

using LR.Application.Hubs;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Services;

/// <summary>
/// Publishes engine-build progress events to SignalR clients via IHubContext (mirrors
/// <see cref="ModelDownloadProgressPublisher"/>).
/// </summary>
public class EngineBuildProgressPublisher : IEngineBuildProgressPublisher
{
    private readonly IHubContext<EngineBuildHub> _hubContext;

    public EngineBuildProgressPublisher(IHubContext<EngineBuildHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task PublishAsync(EngineBuildProgress progress)
    {
        await _hubContext.Clients.All.SendAsync("ReceiveBuildProgress", progress);
    }
}
