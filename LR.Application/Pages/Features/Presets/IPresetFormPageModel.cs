using LR.Core.Models;

namespace LR.Application.Pages.Features.Presets;

/// <summary>
/// Implemented by the Create and Edit page models so both can render the same
/// <c>_PresetForm</c> partial instead of maintaining two near-identical Razor files.
/// </summary>
public interface IPresetFormPageModel
{
    PresetViewModel ViewModel { get; }
    IReadOnlyList<ServerInstance> Servers { get; }
    IReadOnlyList<LocalModel> Models { get; }
    IReadOnlyList<ChatTemplateVariable> DetectedTemplateVariables { get; }
}
