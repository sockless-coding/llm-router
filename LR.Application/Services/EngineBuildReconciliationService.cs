using LR.Core.Interfaces;

namespace LR.Application.Services;

/// <summary>
/// Boot-time pass for the engine-build registry: seeds the built-in compile recipes and marks any
/// build whose install folder has gone missing. Invoked explicitly from Program.cs before
/// app.Run() — not a BackgroundService — mirroring <see cref="ModelLibraryReconciliationService"/>.
/// </summary>
public class EngineBuildReconciliationService
{
    private readonly IEngineBuildManager _manager;
    private readonly ILogger<EngineBuildReconciliationService> _logger;

    public EngineBuildReconciliationService(IEngineBuildManager manager, ILogger<EngineBuildReconciliationService> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        try
        {
            await _manager.SeedBuiltInRecipesAsync(ct);
            await _manager.ReconcileAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Engine build reconciliation failed on startup.");
        }
    }
}
