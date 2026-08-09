using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Reads/writes the single <see cref="ModelLibrarySettings"/> row. Safe to inject into
/// singletons — implementations reach the (scoped) DbContext internally rather than requiring
/// a scoped lifetime themselves.
/// </summary>
public interface IModelLibrarySettingsService
{
    Task<ModelLibrarySettings> GetAsync(CancellationToken ct = default);

    Task SaveAsync(string rootFolder, string? huggingFaceApiToken, CancellationToken ct = default);
}
