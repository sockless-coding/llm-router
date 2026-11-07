using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;

namespace LR.Application.Pages.Api;

public static class PresetModalityExtensions
{
    /// <summary>
    /// The presets whose server's engine generates <paramref name="modality"/> — e.g. only chat
    /// models for listings chat clients read, so an image model never shows up in a chat picker.
    /// A preset whose server is gone counts as text.
    /// </summary>
    public static IEnumerable<ModelPreset> OfModality(
        this IEnumerable<ModelPreset> presets, IEnumerable<ServerInstance> servers, IEngineCatalog engines, EngineModality modality)
    {
        var engineByServer = servers.ToDictionary(s => s.Id, s => s.Engine);
        return presets.Where(p =>
            (engineByServer.TryGetValue(p.ServerInstanceId, out var engine) ? engines.ModalityOf(engine) : EngineModality.Text) == modality);
    }

    /// <summary>
    /// The text presets a chat client can talk to: <see cref="OfModality"/> Text, minus native
    /// decision models that only answer <c>/v1/systemone</c> — llama-server rejects
    /// chat/completions for them.
    /// </summary>
    public static IEnumerable<ModelPreset> ForChat(
        this IEnumerable<ModelPreset> presets, IEnumerable<ServerInstance> servers, IEngineCatalog engines) =>
        presets.OfModality(servers, engines, EngineModality.Text)
            .Where(p => !ModelKindClassifier.IsDecisionOnly(p.GgufDecisionType));

    /// <summary>
    /// The presets that answer <c>/v1/systemone</c>: text presets whose model is a native
    /// decision model (<see cref="ModelPreset.GgufDecisionType"/> set).
    /// </summary>
    public static IEnumerable<ModelPreset> ForDecisions(
        this IEnumerable<ModelPreset> presets, IEnumerable<ServerInstance> servers, IEngineCatalog engines) =>
        presets.OfModality(servers, engines, EngineModality.Text)
            .Where(p => !string.IsNullOrWhiteSpace(p.GgufDecisionType));
}
