using Microsoft.Extensions.DependencyInjection;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Backs <see cref="EngineBuildSettings"/> with its single-row SQLite table. Singleton — reaches
/// the (scoped) DbContext via <see cref="IServiceScopeFactory"/> per call, following the same
/// pattern as <see cref="ModelLibrarySettingsService"/>.
/// </summary>
public class EngineBuildSettingsService : IEngineBuildSettingsService
{
    private const int SingletonRowId = 1;

    private readonly IServiceScopeFactory _scopeFactory;

    public EngineBuildSettingsService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<EngineBuildSettings> GetAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var settings = await context.EngineBuildSettings.FindAsync(new object?[] { SingletonRowId }, ct);
        return settings ?? new EngineBuildSettings();
    }

    public async Task SaveAsync(string installRootFolder, string? buildWorkspaceFolder, string? gitHubApiToken, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();

        var settings = await context.EngineBuildSettings.FindAsync(new object?[] { SingletonRowId }, ct);
        if (settings is null)
        {
            settings = new EngineBuildSettings { Id = SingletonRowId };
            context.EngineBuildSettings.Add(settings);
        }

        settings.InstallRootFolder = installRootFolder;
        settings.BuildWorkspaceFolder = buildWorkspaceFolder ?? string.Empty;
        settings.GitHubApiToken = gitHubApiToken;
        await context.SaveChangesAsync(ct);
    }
}
