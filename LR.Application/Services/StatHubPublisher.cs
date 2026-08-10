using Microsoft.AspNetCore.SignalR;

using LR.Application.Hubs;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Services;

/// <summary>
/// Publishes newly recorded inference statistics to SignalR clients via IHubContext.
/// </summary>
public class StatHubPublisher : IStatHubPublisher
{
    private readonly IHubContext<StatsHub> _hubContext;

    public StatHubPublisher(IHubContext<StatsHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task PublishAsync(ModelStatistics stat)
    {
        await _hubContext.Clients.All.SendAsync("ReceiveStatUpdate", new
        {
            serverInstanceId = stat.ServerInstanceId,
            presetId = stat.PresetId,
            timestamp = stat.Timestamp,
            promptTokensProcessed = stat.PromptTokensProcessed,
            promptTokensPerSec = stat.PromptTokensPerSec,
            generatedTokenCount = stat.GeneratedTokenCount,
            genTokensPerSec = stat.GenTokensPerSec,
            totalLatencyMs = stat.TotalLatencyMs,
            contextLengthUsed = stat.ContextLengthUsed,
        });
    }
}
