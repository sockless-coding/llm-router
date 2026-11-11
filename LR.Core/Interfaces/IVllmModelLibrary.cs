using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// The vLLM models (Hugging Face repositories) the router knows about and the recipes saved for each:
/// recipes.vllm.ai renderings for a hardware choice, or hand-written ones. Presets copy a recipe's
/// settings when it's picked, so nothing here is read when a server starts.
/// </summary>
public interface IVllmModelLibrary
{
    /// <summary>Every model with its recipes, by name.</summary>
    Task<IReadOnlyList<VllmModel>> GetModelsAsync(CancellationToken ct = default);

    Task<VllmModel?> GetModelAsync(Guid id, CancellationToken ct = default);

    Task<VllmModel?> FindModelAsync(string hfId, CancellationToken ct = default);

    /// <summary>A recipe with its model.</summary>
    Task<VllmRecipe?> GetRecipeAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Adds <paramref name="hfId"/>, filled in from recipes.vllm.ai and the Hugging Face Hub when they
    /// know it. Returns the existing model when it's already added. Throws
    /// <see cref="InvalidOperationException"/> for an id that isn't <c>org/name</c>, or a repository the
    /// Hub says doesn't exist.
    /// </summary>
    Task<VllmModel> AddModelAsync(string hfId, CancellationToken ct = default);

    Task<bool> UpdateModelNotesAsync(Guid id, string? notes, CancellationToken ct = default);

    /// <summary>Deletes the model and its recipes. Presets made from them keep their settings.</summary>
    Task<bool> DeleteModelAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Saves recipes.vllm.ai's command for <paramref name="selection"/> as a recipe of the model. Throws
    /// <see cref="InvalidOperationException"/> when the site has no recipe, or the selection needs more
    /// than one machine.
    /// </summary>
    Task<VllmRecipe> AddSiteRecipeAsync(Guid modelId, VllmRecipeSelection selection, string? name, CancellationToken ct = default);

    /// <summary>Fetches a site recipe's command again for the same choices. Custom recipes aren't refreshed.</summary>
    Task<VllmRecipe> RefreshSiteRecipeAsync(Guid recipeId, CancellationToken ct = default);

    /// <summary>
    /// Creates (<see cref="VllmRecipe.Id"/> unknown) or updates a recipe from the editor. Editing a site
    /// recipe makes it custom, so a refresh can't overwrite the edit. Throws
    /// <see cref="InvalidOperationException"/> for settings vLLM wouldn't start with.
    /// </summary>
    Task<VllmRecipe> SaveRecipeAsync(VllmRecipe recipe, CancellationToken ct = default);

    Task<VllmRecipe?> DuplicateRecipeAsync(Guid recipeId, CancellationToken ct = default);

    Task<bool> DeleteRecipeAsync(Guid recipeId, CancellationToken ct = default);
}
