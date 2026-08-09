using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Manages the registry of model files (<see cref="LocalModel"/>) available to presets.
/// </summary>
public interface IModelLibrary
{
    Task<IReadOnlyList<LocalModel>> GetAllAsync();

    Task<LocalModel?> GetByIdAsync(Guid id);

    /// <summary>
    /// Registers an existing file on disk as a model, reading its GGUF metadata.
    /// Throws if the file doesn't exist or a model with the same path is already registered.
    /// </summary>
    Task<LocalModel> ImportFromPathAsync(string filePath, string? name = null);

    /// <summary>
    /// Finds .gguf files under <paramref name="folder"/> that aren't already registered.
    /// </summary>
    Task<IReadOnlyList<string>> ScanFolderAsync(string folder);

    /// <summary>
    /// Re-reads GGUF metadata from disk for an existing model. Marks the model
    /// <see cref="ModelStatus.Missing"/> if the file no longer exists.
    /// </summary>
    Task<bool> RefreshMetadataAsync(Guid id);

    /// <summary>
    /// Updates editable fields (name, notes) on an existing model.
    /// </summary>
    Task<bool> UpdateAsync(Guid id, string name, string? notes);

    /// <summary>
    /// Deletes a model from the registry, optionally deleting the underlying file too.
    /// Presets referencing this model have their ModelId cleared (their ModelPath is untouched).
    /// </summary>
    Task<bool> DeleteAsync(Guid id, bool deleteFile);

    /// <summary>
    /// Counts how many presets currently reference this model.
    /// </summary>
    Task<int> GetPresetUsageCountAsync(Guid id);
}
