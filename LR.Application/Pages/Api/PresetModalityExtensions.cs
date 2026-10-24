using LR.Core.Interfaces;
using LR.Core.Models;

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
}
