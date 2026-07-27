using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Preset manager with SQLite persistence via EF Core.
/// </summary>
public class PresetManager : IPresetManager
{
    private readonly LRDbContext _context;

    public PresetManager(LRDbContext context)
    {
        _context = context;
    }

    public async Task<ModelPreset> CreateAsync(ModelPreset preset)
    {
        if (preset.Id == Guid.Empty)
            throw new ArgumentException("Preset must have a valid ID.", nameof(preset));

        _context.ModelPresets.Add(preset);
        await _context.SaveChangesAsync();
        return preset;
    }

    public async Task<bool> UpdateAsync(Guid presetId, ModelPreset updated)
    {
        var existing = await _context.ModelPresets.FindAsync(presetId);
        if (existing is null) return false;

        existing.Name = updated.Name;
        existing.ModelPath = updated.ModelPath;
        existing.ContextLength = updated.ContextLength;
        existing.GpuLayers = updated.GpuLayers;
        existing.Flags = updated.Flags;

        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteAsync(Guid presetId)
    {
        var existing = await _context.ModelPresets.FindAsync(presetId);
        if (existing is null) return false;

        _context.ModelPresets.Remove(existing);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<IReadOnlyList<ModelPreset>> GetByServerInstanceIdAsync(Guid serverInstanceId)
    {
        var presets = await _context.ModelPresets
            .Where(p => p.ServerInstanceId == serverInstanceId)
            .ToListAsync();
        return presets.AsReadOnly();
    }

    public IReadOnlyList<ModelPreset> GetByServerInstanceId(Guid serverInstanceId)
    {
        return _context.ModelPresets
            .Where(p => p.ServerInstanceId == serverInstanceId)
            .ToList().AsReadOnly();
    }

    public async Task<ModelPreset?> GetByIdAsync(Guid presetId)
    {
        return await _context.ModelPresets.FindAsync(presetId);
    }

    public ModelPreset? GetById(Guid presetId)
    {
        return _context.ModelPresets.Find(presetId);
    }
}
