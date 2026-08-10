using Microsoft.AspNetCore.SignalR;

namespace LR.Application.Hubs;

/// <summary>
/// SignalR hub for pushing newly recorded inference statistics to the UI in real-time
/// (see <see cref="ServerHub"/> for the equivalent server-startup-progress hub).
/// Broadcasts are sent via <c>IHubContext&lt;StatsHub&gt;</c> from <see cref="Services.StatHubPublisher"/>;
/// clients only ever listen, they never invoke methods on this hub.
/// </summary>
public class StatsHub : Hub
{
}
