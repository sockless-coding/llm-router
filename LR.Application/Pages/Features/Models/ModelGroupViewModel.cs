using LR.Core.Models;
using LR.Core.Services;

namespace LR.Application.Pages.Features.Models;

/// <summary>
/// One row-group on the Models index page: all <see cref="LocalModel"/> files that look like the
/// same underlying model release (e.g. Hugging Face quant siblings pulled from the same repo),
/// split into the primary text-model files and any auxiliary files (mmproj projector) that ship
/// alongside them and aren't meant to be run standalone.
/// </summary>
public class ModelGroupViewModel
{
    public string DisplayName { get; set; } = string.Empty;
    public string? Architecture { get; set; }
    public string? ParameterSize { get; set; }
    public List<LocalModel> PrimaryModels { get; set; } = new();
    public List<LocalModel> AuxiliaryModels { get; set; } = new();

    public IEnumerable<LocalModel> AllModels => PrimaryModels.Concat(AuxiliaryModels);
    public bool IsGrouped => PrimaryModels.Count + AuxiliaryModels.Count > 1;
    public long TotalSizeBytes => AllModels.Sum(m => m.FileSizeBytes ?? 0);

    /// <summary>
    /// Groups models primarily by Hugging Face repo (all quants of one download share a repo id)
    /// and falls back to the GGUF-reported model name for locally-imported siblings that have no
    /// repo id; a model with neither stays in its own single-member group.
    /// </summary>
    public static IReadOnlyList<ModelGroupViewModel> Build(IEnumerable<LocalModel> models)
    {
        string GroupKeyFor(LocalModel m)
        {
            if (m.Source == ModelSource.HuggingFace && !string.IsNullOrWhiteSpace(m.HfRepoId))
                return "repo:" + m.HfRepoId.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(m.GgufModelName))
                return "name:" + m.GgufModelName.Trim().ToLowerInvariant();
            return "id:" + m.Id;
        }

        return models
            .GroupBy(GroupKeyFor)
            .Select(BuildGroup)
            .OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ModelGroupViewModel BuildGroup(IEnumerable<LocalModel> members)
    {
        var primaries = new List<LocalModel>();
        var auxiliary = new List<LocalModel>();
        foreach (var m in members)
        {
            if (ModelKindClassifier.Classify(m) == ModelFileKind.Primary)
                primaries.Add(m);
            else
                auxiliary.Add(m);
        }

        // A group made up entirely of auxiliary files (e.g. an mmproj imported on its own, with no
        // sibling text model registered) still needs an anchor row — treat the first as primary
        // rather than hiding it.
        if (primaries.Count == 0 && auxiliary.Count > 0)
        {
            primaries.Add(auxiliary[0]);
            auxiliary.RemoveAt(0);
        }

        primaries = primaries.OrderBy(m => m.FileSizeBytes ?? long.MaxValue).ThenBy(m => m.Name).ToList();
        auxiliary = auxiliary.OrderBy(m => ModelKindClassifier.Classify(m)).ThenBy(m => m.Name).ToList();

        var namedGroup = primaries
            .Where(m => !string.IsNullOrWhiteSpace(m.GgufModelName))
            .GroupBy(m => m.GgufModelName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        string displayName;
        if (namedGroup is not null)
        {
            displayName = namedGroup.First().GgufModelName!.Trim();
        }
        else
        {
            var repoId = primaries.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.HfRepoId))?.HfRepoId;
            displayName = !string.IsNullOrWhiteSpace(repoId)
                ? (repoId.Contains('/') ? repoId[(repoId.IndexOf('/') + 1)..] : repoId)
                : primaries[0].Name;
        }

        return new ModelGroupViewModel
        {
            DisplayName = displayName,
            Architecture = primaries.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Architecture))?.Architecture,
            ParameterSize = primaries.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.ParameterSize))?.ParameterSize,
            PrimaryModels = primaries,
            AuxiliaryModels = auxiliary,
        };
    }
}
