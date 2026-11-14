using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>Backs <see cref="SyclKeepAliveSettings"/> with its single-row table, like <see cref="ModelLibrarySettingsService"/>.</summary>
public class SyclKeepAliveSettingsService : ISyclKeepAliveSettingsService
{
    private const int SingletonRowId = 1;

    private readonly IServiceScopeFactory _scopeFactory;

    public SyclKeepAliveSettingsService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<SyclKeepAliveSettings> GetAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var settings = await context.SyclKeepAliveSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == SingletonRowId, ct);
        return settings ?? new SyclKeepAliveSettings();
    }

    public async Task SaveAsync(SyclKeepAliveMode mode, string environmentSetupCommand, string? executablePath, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();

        var settings = await context.SyclKeepAliveSettings.FindAsync(new object?[] { SingletonRowId }, ct);
        if (settings is null)
        {
            settings = new SyclKeepAliveSettings { Id = SingletonRowId };
            context.SyclKeepAliveSettings.Add(settings);
        }

        settings.Mode = mode;
        settings.EnvironmentSetupCommand = environmentSetupCommand.Trim();
        settings.ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? null : executablePath.Trim().Trim('"');
        await context.SaveChangesAsync(ct);
    }
}
