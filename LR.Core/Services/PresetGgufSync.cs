using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Copies a <see cref="LocalModel"/>'s GGUF-derived fields onto a linked <see cref="ModelPreset"/>.
/// Shared by <see cref="PresetManager"/> (when a preset is saved) and <see cref="ModelLibraryManager"/>
/// (when a model is refreshed) so both paths keep every preset referencing that model in sync
/// instead of only the one being edited.
/// </summary>
internal static class PresetGgufSync
{
    public static void ApplyFromModel(ModelPreset preset, LocalModel model)
    {
        preset.GgufArchitecture = model.Architecture;
        preset.GgufModelName = model.GgufModelName;
        preset.GgufParameterSize = model.ParameterSize;
        preset.GgufQuantizationLevel = model.QuantizationLevel;
        preset.GgufContextLength = model.ContextLength;
        preset.GgufEmbeddingLength = model.EmbeddingLength;
        preset.GgufRopeFreqBase = model.RopeFreqBase;
        preset.GgufChatTemplate = model.ChatTemplate;

        // Only fill in a detected projector when the preset hasn't been given one explicitly —
        // never override a user's own Mmproj/MmprojUrl choice (e.g. picking a different quant,
        // or intentionally disabling vision).
        if (string.IsNullOrEmpty(preset.Mmproj) && string.IsNullOrEmpty(preset.MmprojUrl)
            && !string.IsNullOrEmpty(model.DetectedMmprojPath))
        {
            preset.Mmproj = model.DetectedMmprojPath;
        }
    }
}
