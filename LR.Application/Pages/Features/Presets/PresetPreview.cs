using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Providers;

namespace LR.Application.Pages.Features.Presets;

/// <summary>
/// Builds a preview of the llama.cpp command line a <see cref="PresetViewModel"/> would launch
/// with, without persisting anything — backs the live "generated command" panel on the
/// create/edit form. Mirrors the registry-link resolution <see cref="LR.Core.Services.PresetManager"/>
/// does on save, but reads the model library directly instead of writing the preset back to it.
/// </summary>
public static class PresetPreview
{
    /// <summary>
    /// Resolves <see cref="ModelPreset.ModelId"/>, <see cref="ModelPreset.SpecDraftModelId"/> and
    /// <see cref="ModelPreset.MmprojId"/> against the model registry, copying each linked model's
    /// file path onto the corresponding path field. No-ops per-field when unset or unresolvable.
    /// </summary>
    public static async Task ResolveLinkedModelsAsync(ModelPreset preset, IModelLibrary modelLibrary)
    {
        if (preset.ModelId.HasValue)
        {
            var model = await modelLibrary.GetByIdAsync(preset.ModelId.Value);
            if (model != null)
                preset.ModelPath = model.FilePath;
        }

        if (preset.SpecDraftModelId.HasValue)
        {
            var model = await modelLibrary.GetByIdAsync(preset.SpecDraftModelId.Value);
            if (model != null)
                preset.SpecDraftModel = model.FilePath;
        }

        if (preset.MmprojId.HasValue)
        {
            var model = await modelLibrary.GetByIdAsync(preset.MmprojId.Value);
            if (model != null)
                preset.Mmproj = model.FilePath;
        }
    }

    public static object Build(ModelPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.ModelPath))
            preset.ModelPath = "<no model selected>";

        var args = new LlamaCppArgBuilder { Port = 8080 }.Build(preset);
        var commandLine = "llama-server " + string.Join(' ', args.Select(QuoteIfNeeded));

        return new { commandLine, args };
    }

    private static string QuoteIfNeeded(string arg)
    {
        if (arg.Length == 0)
            return "\"\"";

        var needsQuoting = arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '$' or '`' or '\\');
        if (!needsQuoting)
            return arg;

        return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
