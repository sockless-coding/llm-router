using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <inheritdoc cref="IApiKeyRequestContext"/>
public class ApiKeyRequestContext : IApiKeyRequestContext
{
    public ApiKey? CurrentKey { get; set; }

    public bool IsModelAllowed(Guid presetId)
    {
        if (CurrentKey is null || CurrentKey.AllowAllModels)
            return true;

        return CurrentKey.AllowedPresets.Any(a => a.ModelPresetId == presetId);
    }

    public IEnumerable<ModelPreset> FilterAllowed(IEnumerable<ModelPreset> presets)
    {
        if (CurrentKey is null || CurrentKey.AllowAllModels)
            return presets;

        var allowedIds = CurrentKey.AllowedPresets.Select(a => a.ModelPresetId).ToHashSet();
        return presets.Where(p => allowedIds.Contains(p.Id));
    }
}
