using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Models;

namespace LR.Application.Agents;

/// <summary>Secrets for agents: generation, hashing, and constant-time comparison.</summary>
public static class AgentSecrets
{
    public static string Generate(string prefix) =>
        prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    public static bool Matches(string? presented, string expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
    }
}

/// <summary>
/// The single <see cref="AgentSettings"/> row. Singleton that reaches the scoped DbContext per call,
/// like the model-library and engine-build settings services. The enrollment token is created on
/// first use.
/// </summary>
public sealed class AgentSettingsService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AgentSettingsService(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<AgentSettings> GetAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var settings = await context.AgentSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is null)
        {
            settings = new AgentSettings { Id = 1, EnrollmentToken = AgentSecrets.Generate("lre_") };
            context.AgentSettings.Add(settings);
            await context.SaveChangesAsync(ct);
        }
        else if (string.IsNullOrEmpty(settings.EnrollmentToken))
        {
            settings.EnrollmentToken = AgentSecrets.Generate("lre_");
            await context.SaveChangesAsync(ct);
        }
        return settings;
    }

    public async Task<string> RegenerateEnrollmentTokenAsync(CancellationToken ct = default)
    {
        await GetAsync(ct);
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var settings = await context.AgentSettings.FirstAsync(s => s.Id == 1, ct);
        settings.EnrollmentToken = AgentSecrets.Generate("lre_");
        await context.SaveChangesAsync(ct);
        return settings.EnrollmentToken;
    }

    public async Task SetPublicUrlAsync(string? url, CancellationToken ct = default)
    {
        await GetAsync(ct);
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<LRDbContext>();
        var settings = await context.AgentSettings.FirstAsync(s => s.Id == 1, ct);
        settings.PublicUrl = string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
        await context.SaveChangesAsync(ct);
    }

    /// <summary>The URL install commands and scripts point agents at: the configured public URL, else the one the request came in on.</summary>
    public static string ResolveRouterUrl(AgentSettings settings, HttpRequest request) =>
        !string.IsNullOrWhiteSpace(settings.PublicUrl)
            ? settings.PublicUrl!
            : $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
}
