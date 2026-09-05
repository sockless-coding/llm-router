using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Manages the registry of managed llama.cpp builds and the reusable compile recipes. CRUD +
/// reconciliation + update checks. Modelled on <see cref="IModelLibrary"/>.
/// </summary>
public interface IEngineBuildManager
{
    /// <summary>The GitHub repo builds/updates are sourced from ("ggml-org/llama.cpp").</summary>
    string Repo { get; }

    Task<IReadOnlyList<LlamaCppBuild>> GetAllBuildsAsync();
    Task<LlamaCppBuild?> GetBuildAsync(Guid id);

    /// <summary>Removes a build from the registry, optionally deleting its install folder too.
    /// Servers bound to it have their link cleared.</summary>
    Task<bool> DeleteBuildAsync(Guid id, bool deleteFiles);

    /// <summary>How many servers currently point at this build.</summary>
    Task<int> GetServerUsageCountAsync(Guid buildId);

    Task<IReadOnlyList<LlamaCppBuildRecipe>> GetRecipesAsync();
    Task<LlamaCppBuildRecipe?> GetRecipeAsync(Guid id);
    Task<LlamaCppBuildRecipe> SaveRecipeAsync(LlamaCppBuildRecipe recipe);

    /// <summary>Deletes a non-built-in recipe. Built-in templates cannot be deleted.</summary>
    Task<bool> DeleteRecipeAsync(Guid id);

    /// <summary>Inserts the built-in recipe templates if they aren't present yet. Idempotent.</summary>
    Task SeedBuiltInRecipesAsync(CancellationToken ct = default);

    /// <summary>Marks builds whose install folder has gone missing, and refreshes folder sizes.</summary>
    Task ReconcileAsync(CancellationToken ct = default);

    /// <summary>
    /// Compares an installed build's tag/commit against the latest llama.cpp release and returns
    /// the commits/PRs in between.
    /// </summary>
    Task<EngineBuildUpdateStatus> GetUpdateStatusAsync(Guid buildId, CancellationToken ct = default);
}
