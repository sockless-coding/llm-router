using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Application.Services;

/// <summary>
/// Background service that dispatches queued inference requests to available servers.
/// Runs periodically and processes pending queue items when servers are free.
/// </summary>
public class RequestDispatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRequestQueueService _queue;
    private readonly IServerConcurrencyLimiter _limiter;
    private readonly GatewaySettings _settings;
    private readonly ILogger<RequestDispatcherService> _logger;

    public RequestDispatcherService(
        IServiceScopeFactory scopeFactory,
        IRequestQueueService queue,
        IServerConcurrencyLimiter limiter,
        GatewaySettings settings,
        ILogger<RequestDispatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue = queue;
        _limiter = limiter;
        _settings = settings;
        _logger = logger;
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
                var presetManager = scope.ServiceProvider.GetRequiredService<IPresetManager>();

                // Get all running healthy servers
                var instances = await serverManager.GetAllInstancesAsync();
                var availableServers = instances
                    .Where(s => s.Status == ServerStatus.Running && s.IsHealthy)
                    .ToList();

                foreach (var server in availableServers)
                {
                    // Only dispatch a queued request to a server that's actually running the
                    // model it asked for (or, for requests with no resolvable model, any server).
                    // Otherwise a request for model B could be silently answered by a server
                    // currently running model A.

                    if (server.Engine != ServerEngine.LlamaCpp)
                    {
                        // Non-llama engines manage their own parallelism — pass through unbounded
                        // (one dequeue per tick, as before).
                        if (_queue.TryDequeueMatching(server.ActivePresetId, out var item))
                        {
                            _ = ProcessRequestOnServer(server, item.Request, serverManager,
                                item.Tcs, ServerConcurrencyLimiter.NoopLease, stoppingToken);
                        }
                        continue;
                    }

                    // Drain as many queued requests as the server has free parallel-request slots.
                    int capacity = ResolveLlamaCapacity(server, serverManager, presetManager);
                    while (_limiter.TryAcquire(server.Id, capacity) is { } lease)
                    {
                        if (_queue.TryDequeueMatching(server.ActivePresetId, out var item))
                        {
                            _ = ProcessRequestOnServer(server, item.Request, serverManager,
                                item.Tcs, lease, stoppingToken);
                        }
                        else
                        {
                            lease.Dispose();
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Log but don't crash the dispatcher
                _logger.LogError(ex, "Request dispatcher loop iteration failed; continuing.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), stoppingToken);
        }
    }

    /// <summary>
    /// Resolves how many requests may be in flight against a llama.cpp server at once: the
    /// server's own reported slot count if known, else the active preset's Parallel (when
    /// positive), else the configured default.
    /// </summary>
    private int ResolveLlamaCapacity(ServerInstance server, IServerManager serverManager, IPresetManager presetManager)
    {
        if ((serverManager.GetProvider(server.Id) as IServerCapacityProvider)?.MaxConcurrentRequests is int reported && reported > 0)
            return reported;

        var preset = server.ActivePresetId is Guid pid ? presetManager.GetById(pid) : null;
        if (preset?.Parallel is int p && p > 0)
            return p;

        return _settings.DefaultParallelSlots;
    }

    /// <summary>
    /// Process a single request on the given server, then release its parallel-request slot.
    /// Sends the request via the provider and records stats.
    /// </summary>
    private async Task ProcessRequestOnServer(
        ServerInstance server,
        RouteRequest request,
        IServerManager serverManager,
        TaskCompletionSource<RouteResponse> tcs,
        IDisposable slotLease,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendToProvider(server, request, serverManager, cancellationToken);

            if (response != null)
            {
                // Record statistics - resolve scoped services per-request (must be a fresh
                // scope here, not one shared with the dispatch loop: that scope's DbContext
                // gets disposed as soon as the loop moves on, since this method is invoked
                // fire-and-forget and typically outlives the loop iteration that started it).
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var presetManager = scope.ServiceProvider.GetRequiredService<IPresetManager>();
                    var statisticsService = scope.ServiceProvider.GetRequiredService<IStatisticsService>();
                    var presetId = request.PresetId ?? server.ActivePresetId;
                    var preset = presetId.HasValue ? presetManager.GetById(presetId.Value) : null;
                    await statisticsService.RecordRequestAsync(server, preset, response, request.ApiKeyId);
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
        finally
        {
            // Free the slot so the dispatcher (or a direct route) can reuse it.
            slotLease.Dispose();
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

        return await provider.SendRequestAsync(request.Payload, request.Protocol, cancellationToken);
    }
}
