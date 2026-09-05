using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Services;

/// <summary>
/// Registry of managed llama.cpp builds and the reusable compile recipes, with SQLite persistence
/// via EF Core. Mirrors <see cref="ModelLibraryManager"/>.
/// </summary>
public class EngineBuildManager : IEngineBuildManager
{
    public const string LlamaCppRepo = "ggml-org/llama.cpp";

    private readonly LRDbContext _context;
    private readonly IGitHubClient _github;

    public EngineBuildManager(LRDbContext context, IGitHubClient github)
    {
        _context = context;
        _github = github;
    }

    public string Repo => LlamaCppRepo;

    public async Task<IReadOnlyList<LlamaCppBuild>> GetAllBuildsAsync()
    {
        var list = await _context.LlamaCppBuilds
            .Include(b => b.Recipe)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();
        return list.AsReadOnly();
    }

    public Task<LlamaCppBuild?> GetBuildAsync(Guid id) =>
        _context.LlamaCppBuilds.Include(b => b.Recipe).FirstOrDefaultAsync(b => b.Id == id);

    public async Task<bool> DeleteBuildAsync(Guid id, bool deleteFiles)
    {
        var build = await _context.LlamaCppBuilds.FindAsync(id);
        if (build is null) return false;

        // Clear the link on any server bound to this build (FK is SetNull, but the manual folder
        // path should also be wiped so the server doesn't silently keep using a deleted folder).
        var boundConfigs = await _context.BackendConfigs.Where(c => c.EngineBuildId == id).ToListAsync();
        foreach (var cfg in boundConfigs)
        {
            cfg.EngineBuildId = null;
            if (string.Equals(cfg.LlamaCppExecutableFolderPath, build.InstallPath, StringComparison.OrdinalIgnoreCase))
                cfg.LlamaCppExecutableFolderPath = null;
        }

        if (deleteFiles && !string.IsNullOrWhiteSpace(build.InstallPath) && Directory.Exists(build.InstallPath))
        {
            try { Directory.Delete(build.InstallPath, recursive: true); }
            catch { /* leave the folder; the row is going away regardless */ }
        }

        _context.LlamaCppBuilds.Remove(build);
        await _context.SaveChangesAsync();
        return true;
    }

    public Task<int> GetServerUsageCountAsync(Guid buildId) =>
        _context.BackendConfigs.CountAsync(c => c.EngineBuildId == buildId);

    public async Task<IReadOnlyList<LlamaCppBuildRecipe>> GetRecipesAsync()
    {
        var list = await _context.LlamaCppBuildRecipes
            .OrderByDescending(r => r.IsBuiltIn)
            .ThenBy(r => r.Name)
            .ToListAsync();
        return list.AsReadOnly();
    }

    public Task<LlamaCppBuildRecipe?> GetRecipeAsync(Guid id) =>
        _context.LlamaCppBuildRecipes.FirstOrDefaultAsync(r => r.Id == id);

    public async Task<LlamaCppBuildRecipe> SaveRecipeAsync(LlamaCppBuildRecipe recipe)
    {
        var existing = recipe.Id != Guid.Empty
            ? await _context.LlamaCppBuildRecipes.FirstOrDefaultAsync(r => r.Id == recipe.Id)
            : null;

        if (existing is null)
        {
            if (recipe.Id == Guid.Empty) recipe.Id = Guid.NewGuid();
            recipe.IsBuiltIn = false; // user-saved recipes are never built-in
            recipe.CreatedAt = recipe.UpdatedAt = DateTime.UtcNow;
            _context.LlamaCppBuildRecipes.Add(recipe);
        }
        else
        {
            existing.Name = recipe.Name;
            existing.Description = recipe.Description;
            existing.BackendType = recipe.BackendType;
            existing.GitRepoUrl = recipe.GitRepoUrl;
            existing.GitRef = recipe.GitRef;
            existing.CMakeArgs = recipe.CMakeArgs;
            existing.CMakeGenerator = recipe.CMakeGenerator;
            existing.BuildConfig = recipe.BuildConfig;
            existing.EnvironmentSetupCommand = recipe.EnvironmentSetupCommand;
            existing.ExtraArtifactGlobs = recipe.ExtraArtifactGlobs;
            existing.UpdatedAt = DateTime.UtcNow;
            recipe = existing;
        }

        await _context.SaveChangesAsync();
        return recipe;
    }

    public async Task<bool> DeleteRecipeAsync(Guid id)
    {
        var recipe = await _context.LlamaCppBuildRecipes.FindAsync(id);
        if (recipe is null || recipe.IsBuiltIn) return false;
        _context.LlamaCppBuildRecipes.Remove(recipe);
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task SeedBuiltInRecipesAsync(CancellationToken ct = default)
    {
        var existing = await _context.LlamaCppBuildRecipes
            .Where(r => r.IsBuiltIn)
            .Select(r => r.Name)
            .ToListAsync(ct);
        var have = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var template in BuiltInRecipeTemplates.All())
        {
            if (have.Contains(template.Name)) continue;
            template.Id = Guid.NewGuid();
            template.IsBuiltIn = true;
            template.CreatedAt = template.UpdatedAt = DateTime.UtcNow;
            _context.LlamaCppBuildRecipes.Add(template);
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var builds = await _context.LlamaCppBuilds.ToListAsync(ct);
        var changed = false;

        foreach (var build in builds)
        {
            // In-flight builds are owned by EngineBuildService — don't touch them.
            if (build.Status is EngineBuildStatus.Downloading or EngineBuildStatus.Building or EngineBuildStatus.Pending)
                continue;

            var serverExists = !string.IsNullOrWhiteSpace(build.InstallPath) &&
                Directory.Exists(build.InstallPath) &&
                (File.Exists(Path.Combine(build.InstallPath, "llama-server.exe")) ||
                 File.Exists(Path.Combine(build.InstallPath, "llama-server")));

            if (!serverExists && build.Status != EngineBuildStatus.Missing)
            {
                build.Status = EngineBuildStatus.Missing;
                build.StatusMessage = "Install folder is no longer on disk.";
                changed = true;
            }
            else if (serverExists && build.Status == EngineBuildStatus.Missing)
            {
                build.Status = EngineBuildStatus.Ready;
                build.StatusMessage = null;
                changed = true;
            }
        }

        if (changed)
            await _context.SaveChangesAsync(ct);
    }

    public async Task<EngineBuildUpdateStatus> GetUpdateStatusAsync(Guid buildId, CancellationToken ct = default)
    {
        var build = await _context.LlamaCppBuilds.FindAsync(new object?[] { buildId }, ct);
        var result = new EngineBuildUpdateStatus { BuildId = buildId };
        if (build is null)
        {
            result.Error = "Build not found.";
            return result;
        }

        var installedRef = build.CommitSha ?? build.VersionTag;
        result.InstalledRef = installedRef;
        if (string.IsNullOrWhiteSpace(installedRef))
        {
            result.Error = "This build has no recorded version to compare against.";
            return result;
        }

        var latest = await _github.GetLatestReleaseAsync(Repo, ct);
        if (latest is null)
        {
            result.Error = "Could not reach GitHub to check for updates.";
            return result;
        }

        result.LatestTag = latest.TagName;
        if (string.Equals(installedRef, latest.TagName, StringComparison.OrdinalIgnoreCase))
        {
            result.UpdateAvailable = false;
            return result;
        }

        var compare = await _github.CompareAsync(Repo, installedRef, latest.TagName, ct);
        if (compare is null)
        {
            result.Error = $"GitHub could not compare '{installedRef}' with '{latest.TagName}'.";
            return result;
        }

        // Compare base=installed, head=latest: GitHub's ahead_by counts commits in head not in
        // base — i.e. how far behind the release we are.
        result.BehindBy = compare.AheadBy;
        result.AheadBy = compare.BehindBy;
        result.CompareUrl = compare.HtmlUrl ?? $"https://github.com/{Repo}/compare/{installedRef}...{latest.TagName}";
        result.UpdateAvailable = compare.AheadBy > 0 || compare.Status is "behind" or "diverged";
        result.Commits = ChangelogParser.ToEntries(compare, Repo);
        return result;
    }
}
