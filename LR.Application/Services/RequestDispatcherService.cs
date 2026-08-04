using Microsoft.Extensions.Hosting;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Services;

/// <summary>
/// Background service that dispatches queued inference requests to available servers.
/// Runs periodically and processes pending queue items when servers are free.
/// </summary>
public class RequestDispatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRequestQueueService _queue;

    public RequestDispatcherService(IServiceScopeFactory scopeFactory, IRequestQueueService queue)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Skip the DB query entirely if there's nothing to dispatch.
                // This avoids flooding SQLite with queries when idle (the loop runs every 50ms).
                if (!_queue.HasPendingRequests)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), stoppingToken);
                    continue;
                }

                using var scope = _scopeFactory.CreateScope();
                var serverManager = scope.ServiceProvider.GetRequiredService<IServerManager>();
                var statisticsService = scope.ServiceProvider.GetRequiredService<IStatisticsService>();

                // Get all running healthy servers
                var instances = await serverManager.GetAllInstancesAsync();
                var availableServers = instances
                    .Where(s => s.Status == ServerStatus.Running && s.IsHealthy)
                    .ToList();

                foreach (var server in availableServers)
                {
                    if (_queue.TryDequeue(out var item))
                    {
                        // Check that the request wasn't cancelled while waiting
                        if (!item.Tcs.Task.IsCanceled)
                        {
                            _ = ProcessRequestOnServer(server, item.Request, serverManager,
                                item.Tcs, statisticsService, stoppingToken);
                        }
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Log but don't crash the dispatcher
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), stoppingToken);
        }
    }

    /// <summary>
    /// Process a single request on the given server.
    /// Marks server busy, sends request via provider, records stats, marks server free.
    /// </summary>
    private async Task ProcessRequestOnServer(
        ServerInstance server,
        RouteRequest request,
        IServerManager serverManager,
        TaskCompletionSource<RouteResponse> tcs,
        IStatisticsService statisticsService,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendToProvider(server, request, serverManager, cancellationToken);

            if (response != null)
            {
                // Record statistics - resolve scoped services per-request
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var presetManager = scope.ServiceProvider.GetRequiredService<IPresetManager>();
                    var presetId = request.PresetId ?? server.ActivePresetId;
                    var preset = presetId.HasValue ? presetManager.GetById(presetId.Value) : null;
                    await statisticsService.RecordRequestAsync(server, preset, response);
                }
                catch
                {
                    // Stats recording failure shouldn't block the response
                }

                tcs.TrySetResult(response);
            }
            else
            {
                tcs.TrySetException(new InvalidOperationException(
                    $"Backend returned no response from server {server.Name}"));
            }
        }
        catch (OperationCanceledException)
        {
            tcs.TrySetCanceled();
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    /// <summary>
    /// Send the request to the backend provider for this server.
    /// </summary>
    private static async Task<RouteResponse?> SendToProvider(
        ServerInstance server,
        RouteRequest request,
        IServerManager serverManager,
        CancellationToken cancellationToken)
    {
        var provider = serverManager.GetProvider(server.Id);
        if (provider is null)
            throw new InvalidOperationException($"No backend provider registered for instance {server.Name}.");

        return await provider.SendRequestAsync(request.Payload, cancellationToken);
    }
}
