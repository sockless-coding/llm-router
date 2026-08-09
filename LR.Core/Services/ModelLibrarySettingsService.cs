using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Backs <see cref="ModelLibrarySettings"/> with its single-row SQLite table. Singleton — reaches
/// the (scoped) DbContext via <see cref="IServiceScopeFactory"/> per call, following the same
/// pattern as <see cref="ModelDownloadService"/>, so it can be injected into other singletons
/// (e.g. HuggingFaceClient, ModelDownloadService) without a captive-dependency problem.
/// </summary>
public class ModelLibrarySettingsService : IModelLibrarySettingsService
{
    private const int SingletonRowId = 1;

    private readonly IServiceScopeFactory _scopeFactory;

    public ModelLibrarySettingsService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<ModelLibrarySettings> GetAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var settings = await context.ModelLibrarySettings.FindAsync(new object?[] { SingletonRowId }, ct);
        return settings ?? new ModelLibrarySettings();
    }

    public async Task SaveAsync(string rootFolder, string? huggingFaceApiToken, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();

        var settings = await context.ModelLibrarySettings.FindAsync(new object?[] { SingletonRowId }, ct);
        if (settings is null)
        {
            settings = new ModelLibrarySettings { Id = SingletonRowId };
            context.ModelLibrarySettings.Add(settings);
        }

        settings.RootFolder = rootFolder;
        settings.HuggingFaceApiToken = huggingFaceApiToken;
        await context.SaveChangesAsync(ct);
    }
}
