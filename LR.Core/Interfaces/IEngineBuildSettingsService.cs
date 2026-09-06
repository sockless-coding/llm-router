using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Reads/writes the single-row <see cref="EngineBuildSettings"/>. Singleton — reaches the scoped
/// DbContext via IServiceScopeFactory per call, so it can be injected into other singletons
/// (GitHubReleaseClient, EngineBuildService). Mirrors <see cref="IModelLibrarySettingsService"/>.
/// </summary>
public interface IEngineBuildSettingsService
{
    Task<EngineBuildSettings> GetAsync(CancellationToken ct = default);

    Task SaveAsync(string installRootFolder, string? buildWorkspaceFolder, string? gitHubApiToken, CancellationToken ct = default);
}
