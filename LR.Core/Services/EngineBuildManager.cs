using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Services;

/// <summary>
/// Registry of managed engine installs (llama.cpp builds, Strata checkouts) and the reusable
/// llama.cpp compile recipes, with SQLite persistence via EF Core. Engine-specific checks go
/// through each engine's <see cref="IEngineInstallHandler"/>. Mirrors <see cref="ModelLibraryManager"/>.
/// </summary>
public class EngineBuildManager : IEngineBuildManager
{
    public const string LlamaCppRepo = "ggml-org/llama.cpp";

    private readonly LRDbContext _context;
    private readonly IGitHubClient _github;
    private readonly Dictionary<ServerEngine, IEngineInstallHandler> _handlers;

    public EngineBuildManager(LRDbContext context, IGitHubClient github, IEnumerable<IEngineInstallHandler> handlers)
    {
        _context = context;
        _github = github;
        _handlers = handlers.ToDictionary(h => h.Engine);
    }

    public string Repo => LlamaCppRepo;

    public async Task<IReadOnlyList<EngineBuild>> GetAllBuildsAsync()
    {
        var list = await _context.EngineBuilds
            .Include(b => b.Recipe)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();
        return list.AsReadOnly();
    }

    public async Task<IReadOnlyList<EngineBuild>> GetBuildsAsync(ServerEngine engine)
    {
        var list = await _context.EngineBuilds
            .Include(b => b.Recipe)
            .Where(b => b.Engine == engine)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();
        return list.AsReadOnly();
    }

    public Task<EngineBuild?> GetBuildAsync(Guid id) =>
        _context.EngineBuilds.Include(b => b.Recipe).FirstOrDefaultAsync(b => b.Id == id);

    public async Task<bool> DeleteBuildAsync(Guid id, bool deleteFiles)
    {
        var build = await _context.EngineBuilds.FindAsync(id);
        if (build is null) return false;

        // A git checkout (Strata) holds the user's models and settings next to the engine: it is
        // only ever unregistered, never deleted, and bound servers keep pointing at the folder.
        bool keepsFiles = build.Source == EngineBuildSource.GitCheckout;
        if (keepsFiles)
            deleteFiles = false;

        // Clear the link on any server bound to this build (FK is SetNull, but the manual folder
        // path should also be wiped so the server doesn't silently keep using a deleted folder).
        var boundConfigs = await _context.BackendConfigs.Where(c => c.EngineBuildId == id).ToListAsync();
        foreach (var cfg in boundConfigs)
        {
            cfg.EngineBuildId = null;
            if (keepsFiles)
                cfg.InstallFolderPath = build.InstallPath;
            else if (string.Equals(cfg.InstallFolderPath, build.InstallPath, StringComparison.OrdinalIgnoreCase))
                cfg.InstallFolderPath = null;
        }

        if (deleteFiles && !string.IsNullOrWhiteSpace(build.InstallPath) && Directory.Exists(build.InstallPath))
        {
            try { Directory.Delete(build.InstallPath, recursive: true); }
            catch { /* leave the folder; the row is going away regardless */ }
        }

        _context.EngineBuilds.Remove(build);
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
            .ToListAsync(ct);
        var byName = existing.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var template in BuiltInRecipeTemplates.All())
        {
            if (byName.TryGetValue(template.Name, out var current))
            {
                // Keep built-in rows in sync with the templates (they're read-only in the UI —
                // editing one saves a user copy — so overwriting here is safe and lets doc-based
                // fixes reach existing installs).
                if (!SameConfig(current, template))
                {
                    current.Description = template.Description;
                    current.BackendType = template.BackendType;
                    current.GitRepoUrl = template.GitRepoUrl;
                    current.GitRef = template.GitRef;
                    current.CMakeArgs = template.CMakeArgs;
                    current.CMakeGenerator = template.CMakeGenerator;
                    current.BuildConfig = template.BuildConfig;
                    current.EnvironmentSetupCommand = template.EnvironmentSetupCommand;
                    current.ExtraArtifactGlobs = template.ExtraArtifactGlobs;
                    current.UpdatedAt = DateTime.UtcNow;
                }
                continue;
            }

            template.Id = Guid.NewGuid();
            template.IsBuiltIn = true;
            template.CreatedAt = template.UpdatedAt = DateTime.UtcNow;
            _context.LlamaCppBuildRecipes.Add(template);
        }

        await _context.SaveChangesAsync(ct);
    }

    private static bool SameConfig(LlamaCppBuildRecipe a, LlamaCppBuildRecipe b) =>
        a.Description == b.Description &&
        a.BackendType == b.BackendType &&
        a.GitRepoUrl == b.GitRepoUrl &&
        a.GitRef == b.GitRef &&
        a.CMakeGenerator == b.CMakeGenerator &&
        a.BuildConfig == b.BuildConfig &&
        a.EnvironmentSetupCommand == b.EnvironmentSetupCommand &&
        a.CMakeArgs.SequenceEqual(b.CMakeArgs) &&
        a.ExtraArtifactGlobs.SequenceEqual(b.ExtraArtifactGlobs);

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var builds = await _context.EngineBuilds.ToListAsync(ct);
        var changed = false;

        foreach (var build in builds)
        {
            // In-flight builds are owned by EngineBuildService — don't touch them.
            if (build.Status is EngineBuildStatus.Downloading or EngineBuildStatus.Building or EngineBuildStatus.Pending)
                continue;

            // Source builds made under an env setup command (oneAPI setvars) used to record its
            // banner along with the SHA; keep only the trailing commit hash.
            if (build.CommitSha is { } sha && !IsBareSha(sha))
            {
                var m = System.Text.RegularExpressions.Regex.Match(sha, @"\b[0-9a-fA-F]{40}\b", System.Text.RegularExpressions.RegexOptions.RightToLeft);
                build.CommitSha = m.Success ? m.Value : null;
                changed = true;
            }

            if (!_handlers.TryGetValue(build.Engine, out var handler))
                continue;

            var present = !string.IsNullOrWhiteSpace(build.InstallPath) &&
                Directory.Exists(build.InstallPath) &&
                handler.IsInstallPresent(build.InstallPath);

            if (!present)
            {
                if (build.Status != EngineBuildStatus.Missing)
                {
                    build.Status = EngineBuildStatus.Missing;
                    build.StatusMessage = "Install folder is no longer on disk.";
                    changed = true;
                }
                continue;
            }

            var (versionBefore, backendBefore) = (build.VersionTag, build.BackendType);
            handler.RefreshFromDisk(build);
            changed |= build.VersionTag != versionBefore || build.BackendType != backendBefore;

            // Error rows keep their error until the next install/update attempt.
            var expected = handler.IsReady(build.InstallPath) ? EngineBuildStatus.Ready : EngineBuildStatus.NeedsSetup;
            if (build.Status is EngineBuildStatus.Missing or EngineBuildStatus.NeedsSetup or EngineBuildStatus.Ready
                && build.Status != expected)
            {
                build.Status = expected;
                build.StatusMessage = null;
                changed = true;
            }
        }

        if (changed)
            await _context.SaveChangesAsync(ct);
    }

    private static bool IsBareSha(string s) => s.Length is >= 7 and <= 64 && s.All(char.IsAsciiHexDigit);

    public async Task<EngineBuildUpdateStatus> GetUpdateStatusAsync(Guid buildId, CancellationToken ct = default)
    {
        var build = await _context.EngineBuilds.FindAsync(new object?[] { buildId }, ct);
        var result = new EngineBuildUpdateStatus { BuildId = buildId };
        if (build is null)
        {
            result.Error = "Build not found.";
            return result;
        }

        if (!_handlers.TryGetValue(build.Engine, out var handler))
        {
            result.Error = $"Update checks aren't supported for {build.Engine} installs.";
            return result;
        }
        var repo = handler.Repo;

        // A release install is compared tag to tag; anything else by its exact commit.
        var installedRef = build.Engine == ServerEngine.Strata && build.Source == EngineBuildSource.OfficialRelease
            ? build.VersionTag ?? build.CommitSha
            : build.CommitSha ?? build.VersionTag;
        result.InstalledRef = installedRef;
        if (string.IsNullOrWhiteSpace(installedRef))
        {
            result.Error = build.Source == EngineBuildSource.GitCheckout
                ? "No commit is recorded for this install (is it a git clone?). Press Refresh to re-read it."
                : "This build has no recorded version to compare against.";
            return result;
        }

        var latestRef = await handler.GetLatestRefAsync(build, _github, ct);
        if (latestRef is null)
        {
            result.Error = "Could not reach GitHub to check for updates.";
            return result;
        }

        result.LatestTag = latestRef;
        if (string.Equals(installedRef, latestRef, StringComparison.OrdinalIgnoreCase))
        {
            result.UpdateAvailable = false;
            return result;
        }

        var compare = await _github.CompareAsync(repo, installedRef, latestRef, ct);
        if (compare is null)
        {
            result.Error = $"GitHub could not compare '{installedRef}' with '{latestRef}'." +
                (build.Engine == ServerEngine.Strata
                    ? " Strata rewrote its history on 2026-10-06; a clone from before that is moved over by Strata's own UPDATE.bat / update.sh."
                    : "");
            return result;
        }

        // Compare base=installed, head=latest: GitHub's ahead_by counts commits in head not in
        // base — i.e. how far behind the release we are.
        result.BehindBy = compare.AheadBy;
        result.AheadBy = compare.BehindBy;
        result.CompareUrl = compare.HtmlUrl ?? $"https://github.com/{repo}/compare/{installedRef}...{latestRef}";
        result.UpdateAvailable = compare.AheadBy > 0 || compare.Status is "behind" or "diverged";
        result.Commits = ChangelogParser.ToEntries(compare, repo);
        return result;
    }

    public async Task<IReadOnlyList<UpstreamDocReference>> GetUpstreamReferenceAsync(BackendType backend, CancellationToken ct = default)
    {
        var refs = new List<UpstreamDocReference>();
        foreach (var path in UpstreamBuildDocs.SourcesFor(backend))
        {
            var url = UpstreamBuildDocs.GitHubUrlFor(Repo, path);
            try
            {
                var content = await _github.GetRawFileAsync(Repo, UpstreamBuildDocs.DocsRef, path, ct);
                if (content is null)
                {
                    refs.Add(new UpstreamDocReference(path, url, Array.Empty<CommandBlock>(), "Not found upstream."));
                    continue;
                }
                var commands = UpstreamBuildDocs.ExtractCommandBlocks(path, content, backend);
                refs.Add(new UpstreamDocReference(path, url, commands, null));
            }
            catch (Exception ex)
            {
                refs.Add(new UpstreamDocReference(path, url, Array.Empty<CommandBlock>(), ex.Message));
            }
        }
        return refs;
    }
}
