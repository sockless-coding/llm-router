using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// A Hugging Face model repository that vLLM serves directly (it downloads the weights itself, so the
/// router keeps no file), with the recipes saved for it. Unlike a <see cref="LocalModel"/> there's
/// nothing on disk: the model is its <see cref="HfId"/>, and each <see cref="VllmRecipe"/> is one tested
/// way of running it on some hardware.
/// </summary>
[Table("VllmModels")]
public class VllmModel
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The Hugging Face repository, <c>org/name</c>. Unique.</summary>
    [Required, MaxLength(256)]
    public string HfId { get; set; } = string.Empty;

    [Required, MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Who publishes the model (the recipe site's provider, or the repository's owner).</summary>
    [MaxLength(128)]
    public string? Provider { get; set; }

    [MaxLength(4096)]
    public string? Description { get; set; }

    /// <summary>The model's native context window, when the recipe site knows it.</summary>
    public int? ContextLength { get; set; }

    [MaxLength(32)]
    public string? MinVllmVersion { get; set; }

    /// <summary>The Hugging Face pipeline tag (<c>text-generation</c>, <c>image-text-to-text</c>, …).</summary>
    [MaxLength(64)]
    public string? PipelineTag { get; set; }

    /// <summary>Whether recipes.vllm.ai had a recipe for this model when it was added or last checked.</summary>
    public bool HasSiteRecipe { get; set; }

    [MaxLength(2048)]
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<VllmRecipe> Recipes { get; set; } = new List<VllmRecipe>();
}

/// <summary>Where a <see cref="VllmRecipe"/>'s settings came from.</summary>
public enum VllmRecipeOrigin
{
    /// <summary>Rendered by recipes.vllm.ai for a hardware/variant/strategy choice; can be refreshed from it.</summary>
    Site = 0,

    /// <summary>Written (or edited) by hand; the router never changes it.</summary>
    Custom = 1,
}

/// <summary>
/// One saved way of running a <see cref="VllmModel"/>: the vLLM settings a preset gets when it starts
/// from this recipe (keys of <see cref="Services.EngineBuilds.VllmPresetSettings"/>). A preset copies
/// them when the recipe is picked and remembers which recipe it was, so editing a recipe later doesn't
/// change presets already made from it.
/// </summary>
[Table("VllmRecipes")]
public class VllmRecipe
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid VllmModelId { get; set; }

    [ForeignKey(nameof(VllmModelId))]
    public VllmModel? VllmModel { get; set; }

    [Required, MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    public VllmRecipeOrigin Origin { get; set; } = VllmRecipeOrigin.Custom;

    /// <summary>The recipe site's hardware profile id (<c>arc_pro_b70</c>), or null for any hardware.</summary>
    [MaxLength(64)]
    public string? Hardware { get; set; }

    [MaxLength(64)]
    public string? Variant { get; set; }

    [MaxLength(64)]
    public string? Strategy { get; set; }

    /// <summary>The recipe site features chosen, comma-separated; null keeps the site's defaults.</summary>
    [MaxLength(512)]
    public string? Features { get; set; }

    /// <summary>
    /// What <c>vllm serve</c> loads: usually the model's <see cref="VllmModel.HfId"/>, but a variant can
    /// serve another repository (an FP8 or GPTQ upload), and a custom recipe can point at a model folder.
    /// </summary>
    [Required, MaxLength(1024)]
    public string ServedModel { get; set; } = string.Empty;

    /// <summary>The preset settings this recipe fills in. Stored as JSON.</summary>
    [Column(TypeName = "TEXT")]
    public Dictionary<string, string> Settings { get; set; } = new();

    /// <summary>What to read before using the recipe, one per line (from the recipe site, or written by hand).</summary>
    [Column(TypeName = "TEXT")]
    public string? Warnings { get; set; }

    /// <summary>The minimum VRAM the recipe's variant needs, in GB.</summary>
    public double? VramGb { get; set; }

    /// <summary>The recipe's page on recipes.vllm.ai.</summary>
    [MaxLength(1024)]
    public string? PageUrl { get; set; }

    [MaxLength(2048)]
    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the settings were last fetched from recipes.vllm.ai.</summary>
    public DateTimeOffset? FetchedAt { get; set; }

    public IEnumerable<string> WarningLines =>
        (Warnings ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
