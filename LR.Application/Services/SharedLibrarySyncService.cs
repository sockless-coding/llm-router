using LR.Core.Services;

namespace LR.Application.Services;

/// <summary>
/// Runs <see cref="SharedLibraryDiscovery"/> shortly after startup and then every few minutes, so engine
/// installs and model files that another router instance puts in a shared install root or model root
/// show up here without a manual import, linked to their Hugging Face repo.
/// </summary>
public class SharedLibrarySyncService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SharedLibrarySyncService> _logger;

    public SharedLibrarySyncService(IServiceScopeFactory scopeFactory, ILogger<SharedLibrarySyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var discovery = scope.ServiceProvider.GetRequiredService<SharedLibraryDiscovery>();
            var result = await discovery.RunAsync(retryHubLookups: false, ct);
            if (result is { EnginesAdded: > 0 } or { ModelsAdded: > 0 } or { ModelsLinked: > 0 } or { ModelsMissing: > 0 } or { ModelsRestored: > 0 })
                _logger.LogInformation("Shared library sync: {Summary}", result.Summary());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Shared library sync failed.");
        }
    }
}
