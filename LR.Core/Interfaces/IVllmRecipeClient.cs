namespace LR.Core.Interfaces;

/// <summary>
/// Reads the vLLM recipes published at recipes.vllm.ai: which models have one, the choices each offers,
/// and the <c>vllm serve</c> command for a set of choices. Used when filling in a preset, by the router
/// only — agents never talk to the recipe site, and a saved preset doesn't depend on it.
/// </summary>
public interface IVllmRecipeClient
{
    /// <summary>Every recipe plus the hardware and strategy vocabulary. Cached for an hour.</summary>
    Task<VllmRecipeCatalog> GetCatalogAsync(CancellationToken ct = default);

    /// <summary>One recipe's choices, or null if there's no recipe for <paramref name="hfId"/>.</summary>
    Task<VllmRecipeDetails?> GetRecipeAsync(string hfId, CancellationToken ct = default);

    /// <summary>The command for the given choices (null = the recipe's default), or null if there's no such recipe.</summary>
    Task<VllmRecipeRendering?> RenderAsync(string hfId, VllmRecipeSelection selection, CancellationToken ct = default);
}

public sealed record VllmRecipeCatalog(
    IReadOnlyList<VllmRecipeSummary> Models,
    IReadOnlyList<VllmHardwareProfile> Hardware,
    IReadOnlyList<VllmStrategy> Strategies);

public sealed record VllmRecipeSummary(string HfId, string Title, string Provider);

/// <param name="Id">Profile id, e.g. <c>rtx_5090</c>.</param>
/// <param name="DisplayName">e.g. "RTX 5090".</param>
/// <param name="Brand">e.g. "NVIDIA".</param>
/// <param name="GpuCount">GPUs the profile assumes; a recipe's <c>--tensor-parallel-size</c> follows from it.</param>
/// <param name="VramGb">Total VRAM of the profile.</param>
public sealed record VllmHardwareProfile(string Id, string DisplayName, string Brand, int GpuCount, int VramGb);

/// <summary>A single-machine serving strategy. Multi-node ones aren't listed: a router server is one machine.</summary>
public sealed record VllmStrategy(string Id, string DisplayName, string? Description);

public sealed record VllmRecipeVariant(string Key, string? Label, string? Precision, double? VramMinimumGb, string? Description, string? ModelId, IReadOnlyList<string> SupportedHardware);

public sealed record VllmRecipeFeature(string Key, string? Description, bool OptIn);

public sealed record VllmRecipeDetails(
    string HfId,
    string Title,
    string? Description,
    string? MinVllmVersion,
    int? ContextLength,
    IReadOnlyList<string> Tasks,
    string? DefaultHardware,
    IReadOnlyList<VllmRecipeVariant> Variants,
    IReadOnlyList<VllmRecipeFeature> Features,
    string? Dependencies);

public sealed record VllmRecipeSelection(string? Hardware = null, string? Variant = null, string? Strategy = null, IReadOnlyList<string>? Features = null);

/// <summary>A choice the recipe site changed because the recipe can't run that way.</summary>
public sealed record VllmRecipeAdjustment(string Param, string? Requested, string? Used, string? Reason);

public sealed record VllmRecipeRendering(
    string HfId,
    string Hardware,
    string Strategy,
    string Variant,
    string DeployType,
    IReadOnlyList<string> Argv,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<VllmRecipeAdjustment> Adjustments,
    string? PageUrl);
