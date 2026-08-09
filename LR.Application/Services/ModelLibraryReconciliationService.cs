using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;

namespace LR.Application.Services;

/// <summary>
/// Boot-time reconciliation pass: for every preset whose ModelPath isn't linked to the model
/// registry yet, registers the file (if it still exists on disk) as a <see cref="Core.Models.LocalModel"/>
/// and links the preset to it. Lets existing installs get a populated registry without a manual
/// migration step. Invoked explicitly from Program.cs before app.Run() — not a BackgroundService —
/// mirroring <see cref="WrapperReconciliationService"/>.
/// </summary>
public class ModelLibraryReconciliationService
{
    private readonly LRDbContext _context;
    private readonly IModelLibrary _modelLibrary;
    private readonly ILogger<ModelLibraryReconciliationService> _logger;

    public ModelLibraryReconciliationService(LRDbContext context, IModelLibrary modelLibrary, ILogger<ModelLibraryReconciliationService> logger)
    {
        _context = context;
        _modelLibrary = modelLibrary;
        _logger = logger;
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var unlinkedPresets = await _context.ModelPresets
            .Where(p => p.ModelId == null && p.ModelPath != "")
            .ToListAsync(ct);

        if (unlinkedPresets.Count == 0)
            return;

        var existingModels = await _context.LocalModels.ToListAsync(ct);
        var byPath = existingModels.ToDictionary(m => m.FilePath, StringComparer.OrdinalIgnoreCase);

        var groups = unlinkedPresets.GroupBy(p => NormalizePath(p.ModelPath));
        foreach (var group in groups)
        {
            var path = group.Key;

            if (!byPath.TryGetValue(path, out var model))
            {
                if (!File.Exists(path))
                    continue; // nothing to register — the file isn't there

                try
                {
                    model = await _modelLibrary.ImportFromPathAsync(path);
                    byPath[path] = model;
                    _logger.LogInformation("Registered existing model file {Path} into the model library.", path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to auto-register model file {Path} into the model library.", path);
                    continue;
                }
            }

            foreach (var preset in group)
                preset.ModelId = model.Id;
        }

        await _context.SaveChangesAsync(ct);
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
