using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Thin client over the public Hugging Face Hub API — search, file listing, revision lookup,
/// and file download.
/// </summary>
public interface IHuggingFaceClient
{
    /// <summary>
    /// Searches for models on the hub, filtered to repos that contain GGUF files.
    /// </summary>
    Task<IReadOnlyList<HfModelSummary>> SearchModelsAsync(string query, int limit = 20, CancellationToken ct = default);

    /// <summary>
    /// Gets full repo detail (current revision + file list with sizes) for a repo.
    /// </summary>
    Task<HfRepoDetail?> GetRepoDetailAsync(string repoId, CancellationToken ct = default);

    /// <summary>
    /// Lists just the .gguf files in a repo (a convenience filter over <see cref="GetRepoDetailAsync"/>).
    /// </summary>
    Task<IReadOnlyList<HfRepoFile>> ListGgufFilesAsync(string repoId, CancellationToken ct = default);

    /// <summary>
    /// Downloads a single file from a repo to <paramref name="destinationPath"/>, reporting
    /// progress as bytes arrive. Returns the resolved commit SHA the file was downloaded at.
    /// </summary>
    Task<string?> DownloadFileAsync(
        string repoId,
        string filename,
        string revision,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct = default);
}
