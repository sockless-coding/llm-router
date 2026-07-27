using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// In-memory preset manager. Persists presets per server instance.
/// File/DB persistence is deferred to a future phase.
/// </summary>
public class PresetManager : IPresetManager
{
    private readonly Dictionary<Guid, ModelPreset> _presets = new();

    public Task<ModelPreset> CreateAsync(ModelPreset preset)
    {
        if (preset.Id == Guid.Empty)
            throw new ArgumentException("Preset must have a valid ID.", nameof(preset));

        _presets[preset.Id] = preset;
        return Task.FromResult(preset);
    }

    public Task<bool> UpdateAsync(Guid presetId, ModelPreset updated)
    {
        if (!_presets.ContainsKey(presetId))
            return Task.FromResult(false);

        updated.Id = presetId;
        _presets[presetId] = updated;
        return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(Guid presetId)
    {
        var removed = _presets.Remove(presetId);
        return Task.FromResult(removed);
    }

    public IReadOnlyList<ModelPreset> GetByServerInstanceId(Guid serverInstanceId)
    {
        return _presets.Values.Where(p => p.ServerInstanceId == serverInstanceId).ToList().AsReadOnly();
    }

    public ModelPreset? GetById(Guid presetId) => _presets.GetValueOrDefault(presetId);
}
