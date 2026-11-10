using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Services;

public static class PresetContext
{
    /// <summary>
    /// The context window the preset's server is launched with: llama.cpp's <c>-c</c>
    /// (<see cref="ModelPreset.ContextSize"/>) or vLLM's <c>--max-model-len</c>. Null when neither is set,
    /// i.e. the server picks it from the model.
    /// </summary>
    public static int? ConfiguredContextSize(this ModelPreset preset) =>
        preset.ContextSize is > 0 ? preset.ContextSize.Value : VllmPresetSettings.MaxModelLenTokens(preset);

    /// <summary>
    /// The context window to report to clients: what the running server says it resolved (vLLM's
    /// <c>max_model_len</c>) when the preset's server is serving this preset right now, otherwise
    /// <see cref="ConfiguredContextSize"/>.
    /// </summary>
    public static int? ServedContextSize(this ModelPreset preset, IServerManager servers)
    {
        var instance = servers.GetAllInstances().FirstOrDefault(i => i.Id == preset.ServerInstanceId);
        if (instance?.ActivePresetId == preset.Id &&
            servers.GetProvider(preset.ServerInstanceId) is IServerCapacityProvider { ServerProps.MaxModelLength: int live })
            return live;
        return preset.ConfiguredContextSize();
    }
}
