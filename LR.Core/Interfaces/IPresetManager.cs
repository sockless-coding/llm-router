using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Manages model presets for server instances.
/// </summary>
public interface IPresetManager
{
    /// <summary>
    /// Creates a new preset and associates it with the given server instance.
    /// </summary>
    Task<ModelPreset> CreateAsync(ModelPreset preset);

    /// <summary>
    /// Updates an existing preset in place.
    /// </summary>
    Task<bool> UpdateAsync(Guid presetId, ModelPreset updated);

    /// <summary>
    /// Deletes a preset by ID.
    /// </summary>
    Task<bool> DeleteAsync(Guid presetId);

    /// <summary>
    /// Gets all presets for a specific server instance (async).
    /// </summary>
    Task<IReadOnlyList<ModelPreset>> GetByServerInstanceIdAsync(Guid serverInstanceId);

    /// <summary>
    /// Gets all presets for a specific server instance.
    /// </summary>
    IReadOnlyList<ModelPreset> GetByServerInstanceId(Guid serverInstanceId);

    /// <summary>
    /// Gets a single preset by ID, or null if not found (async).
    /// </summary>
    Task<ModelPreset?> GetByIdAsync(Guid presetId);

    /// <summary>
    /// Gets a single preset by ID, or null if not found.
    /// </summary>
    ModelPreset? GetById(Guid presetId);

    /// <summary>
    /// Gets all presets across all server instances (async).
    /// </summary>
    Task<IReadOnlyList<ModelPreset>> GetAllPresetsAsync();

    /// <summary>
    /// Gets all presets across all server instances.
    /// </summary>
    IReadOnlyList<ModelPreset> GetAllPresets();
}
