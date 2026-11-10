using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Services;

/// <inheritdoc cref="IVllmModelLibrary"/>
public sealed partial class VllmModelLibrary : IVllmModelLibrary
{
    private readonly LRDbContext _context;
    private readonly IVllmRecipeClient _recipes;
    private readonly IHuggingFaceClient _hub;
    private readonly ILogger<VllmModelLibrary> _logger;

    public VllmModelLibrary(LRDbContext context, IVllmRecipeClient recipes, IHuggingFaceClient hub, ILogger<VllmModelLibrary> logger)
    {
        _context = context;
        _recipes = recipes;
        _hub = hub;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VllmModel>> GetModelsAsync(CancellationToken ct = default)
    {
        var models = await _context.VllmModels.Include(m => m.Recipes).AsNoTracking().ToListAsync(ct);
        foreach (var model in models)
            model.Recipes = OrderRecipes(model.Recipes);
        return models.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<VllmModel?> GetModelAsync(Guid id, CancellationToken ct = default)
    {
        var model = await _context.VllmModels.Include(m => m.Recipes).AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (model is not null)
            model.Recipes = OrderRecipes(model.Recipes);
        return model;
    }

    public async Task<VllmModel?> FindModelAsync(string hfId, CancellationToken ct = default)
    {
        var id = hfId.Trim();
        // HfId is unique case-insensitively in practice (Hugging Face ids are), but SQLite compares
        // case-sensitively, so look the way a user would type it.
        return (await _context.VllmModels.AsNoTracking().ToListAsync(ct))
            .FirstOrDefault(m => string.Equals(m.HfId, id, StringComparison.OrdinalIgnoreCase));
    }

    public Task<VllmRecipe?> GetRecipeAsync(Guid id, CancellationToken ct = default) =>
        _context.VllmRecipes.Include(r => r.VllmModel).AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<VllmModel> AddModelAsync(string hfId, CancellationToken ct = default)
    {
        var id = hfId?.Trim() ?? "";
        if (!HfIdRegex().IsMatch(id) || id.Contains(".."))
            throw new InvalidOperationException($"'{hfId}' isn't a Hugging Face model id (org/name).");
        if (await FindModelAsync(id, ct) is { } existing)
            return existing;

        VllmRecipeDetails? recipe = null;
        VllmRecipeSummary? summary = null;
        try
        {
            recipe = await _recipes.GetRecipeAsync(id, ct);
            if (recipe is not null)
                summary = (await _recipes.GetCatalogAsync(ct)).Models.FirstOrDefault(m => string.Equals(m.HfId, id, StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidOperationException ex)
        {
            // The model is still worth adding; a site recipe can be added once the site answers.
            _logger.LogWarning(ex, "Could not read the vLLM recipe for {HfId}.", id);
        }

        HfRepoDetail? repo = null;
        try
        {
            repo = await _hub.GetRepoDetailAsync(id, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not read the Hugging Face repository {HfId}.", id);
        }

        if (recipe is null && repo is null)
            throw new InvalidOperationException(
                $"Neither recipes.vllm.ai nor the Hugging Face Hub knows {id}. Check the id; a gated repository also needs a Hugging Face token in the model library settings.");

        var model = new VllmModel
        {
            // The site's capitalization can differ from the Hub's (Google/… vs google/…); vLLM wants the Hub's.
            HfId = repo?.Id is { Length: > 0 } hubId ? hubId : id,
            Name = recipe?.Title is { Length: > 0 } title && title != recipe.HfId ? title : id[(id.IndexOf('/') + 1)..],
            Provider = summary?.Provider is { Length: > 0 } provider ? provider : repo?.Author ?? id[..id.IndexOf('/')],
            Description = Truncate(recipe?.Description?.Trim(), 4096),
            ContextLength = recipe?.ContextLength,
            MinVllmVersion = recipe?.MinVllmVersion,
            PipelineTag = repo?.PipelineTag,
            HasSiteRecipe = recipe is not null,
        };
        _context.VllmModels.Add(model);
        await _context.SaveChangesAsync(ct);
        return model;
    }

    public async Task<bool> UpdateModelNotesAsync(Guid id, string? notes, CancellationToken ct = default)
    {
        var model = await _context.VllmModels.FindAsync([id], ct);
        if (model is null) return false;
        model.Notes = string.IsNullOrWhiteSpace(notes) ? null : Truncate(notes.Trim(), 2048);
        model.UpdatedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteModelAsync(Guid id, CancellationToken ct = default)
    {
        var model = await _context.VllmModels.FindAsync([id], ct);
        if (model is null) return false;
        _context.VllmModels.Remove(model);
        await _context.SaveChangesAsync(ct);
        return true;
    }

    public async Task<VllmRecipe> AddSiteRecipeAsync(Guid modelId, VllmRecipeSelection selection, string? name, CancellationToken ct = default)
    {
        var model = await _context.VllmModels.FindAsync([modelId], ct)
            ?? throw new InvalidOperationException("That model isn't in the library any more.");
        var recipe = new VllmRecipe { VllmModelId = model.Id, Origin = VllmRecipeOrigin.Site, Features = FeaturesText(selection.Features) };
        await RenderIntoAsync(recipe, model, selection, ct);
        recipe.Name = string.IsNullOrWhiteSpace(name) ? await DefaultNameAsync(recipe, ct) : Truncate(name.Trim(), 256)!;
        _context.VllmRecipes.Add(recipe);
        model.HasSiteRecipe = true;
        model.UpdatedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(ct);
        return recipe;
    }

    public async Task<VllmRecipe> RefreshSiteRecipeAsync(Guid recipeId, CancellationToken ct = default)
    {
        var recipe = await _context.VllmRecipes.Include(r => r.VllmModel).FirstOrDefaultAsync(r => r.Id == recipeId, ct)
            ?? throw new InvalidOperationException("That recipe isn't in the library any more.");
        if (recipe.Origin != VllmRecipeOrigin.Site)
            throw new InvalidOperationException("Only recipes from recipes.vllm.ai can be refreshed; this one was written by hand.");

        var features = recipe.Features is null ? null
            : recipe.Features == "" ? Array.Empty<string>()
            : recipe.Features.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        await RenderIntoAsync(recipe, recipe.VllmModel!, new VllmRecipeSelection(recipe.Hardware, recipe.Variant, recipe.Strategy, features), ct);
        await _context.SaveChangesAsync(ct);
        return recipe;
    }

    public async Task<VllmRecipe> SaveRecipeAsync(VllmRecipe input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
            throw new InvalidOperationException("Give the recipe a name.");
        if (string.IsNullOrWhiteSpace(input.ServedModel))
            throw new InvalidOperationException("Enter the model vLLM serves: a Hugging Face id or a model folder.");

        var settings = input.Settings
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
        VllmPresetSettings.Validate(input.ServedModel.Trim(), settings);

        var recipe = await _context.VllmRecipes.FindAsync([input.Id], ct);
        if (recipe is null)
        {
            if (await _context.VllmModels.FindAsync([input.VllmModelId], ct) is null)
                throw new InvalidOperationException("That model isn't in the library any more.");
            recipe = new VllmRecipe { Id = input.Id, VllmModelId = input.VllmModelId, CreatedAt = DateTimeOffset.UtcNow };
            _context.VllmRecipes.Add(recipe);
        }
        else if (recipe.Origin == VllmRecipeOrigin.Site && !SameRecipe(recipe, input, settings))
        {
            recipe.Origin = VllmRecipeOrigin.Custom;
        }

        recipe.Name = Truncate(input.Name.Trim(), 256)!;
        recipe.Hardware = Blank(input.Hardware);
        recipe.ServedModel = input.ServedModel.Trim();
        recipe.Settings = settings;
        recipe.Warnings = Blank(input.Warnings);
        recipe.VramGb = input.VramGb;
        recipe.Notes = Truncate(Blank(input.Notes), 2048);
        recipe.UpdatedAt = DateTimeOffset.UtcNow;
        await _context.SaveChangesAsync(ct);
        return recipe;
    }

    public async Task<VllmRecipe?> DuplicateRecipeAsync(Guid recipeId, CancellationToken ct = default)
    {
        var source = await _context.VllmRecipes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recipeId, ct);
        if (source is null) return null;
        var siblings = await _context.VllmRecipes.Where(r => r.VllmModelId == source.VllmModelId).Select(r => r.Name).ToListAsync(ct);
        var copy = new VllmRecipe
        {
            VllmModelId = source.VllmModelId,
            Name = UniqueName($"{source.Name} (copy)", siblings),
            // A copy is somewhere to make changes; refreshing it from the site would undo them.
            Origin = VllmRecipeOrigin.Custom,
            Hardware = source.Hardware,
            Variant = source.Variant,
            Strategy = source.Strategy,
            Features = source.Features,
            ServedModel = source.ServedModel,
            Settings = new Dictionary<string, string>(source.Settings),
            Warnings = source.Warnings,
            VramGb = source.VramGb,
            PageUrl = source.PageUrl,
            Notes = source.Notes,
            FetchedAt = source.FetchedAt,
        };
        _context.VllmRecipes.Add(copy);
        await _context.SaveChangesAsync(ct);
        return copy;
    }

    public async Task<bool> DeleteRecipeAsync(Guid recipeId, CancellationToken ct = default)
    {
        var recipe = await _context.VllmRecipes.FindAsync([recipeId], ct);
        if (recipe is null) return false;
        _context.VllmRecipes.Remove(recipe);
        await _context.SaveChangesAsync(ct);
        return true;
    }

    private async Task RenderIntoAsync(VllmRecipe recipe, VllmModel model, VllmRecipeSelection selection, CancellationToken ct)
    {
        var rendering = await _recipes.RenderAsync(model.HfId, selection, ct)
            ?? throw new InvalidOperationException($"recipes.vllm.ai has no recipe for {model.HfId}.");
        var details = await _recipes.GetRecipeAsync(model.HfId, ct);
        var applied = VllmPresetSettings.Apply(rendering, details, selection.Features);

        recipe.Hardware = Blank(rendering.Hardware);
        recipe.Variant = Blank(rendering.Variant);
        recipe.Strategy = Blank(rendering.Strategy);
        recipe.ServedModel = applied.Model;
        recipe.Settings = new Dictionary<string, string>(applied.Settings);
        recipe.Warnings = applied.Warnings.Count > 0 ? string.Join('\n', applied.Warnings) : null;
        recipe.VramGb = details?.Variants.FirstOrDefault(v => v.Key == rendering.Variant)?.VramMinimumGb;
        recipe.PageUrl = rendering.PageUrl is { Length: > 0 } url
            ? (Uri.TryCreate(url, UriKind.Absolute, out _) ? url : "https://recipes.vllm.ai" + (url.StartsWith('/') ? url : "/" + url))
            : null;
        recipe.FetchedAt = recipe.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task<string> DefaultNameAsync(VllmRecipe recipe, CancellationToken ct)
    {
        string hardware = recipe.Hardware ?? "default hardware";
        try
        {
            if ((await _recipes.GetCatalogAsync(ct)).Hardware.FirstOrDefault(h => h.Id == recipe.Hardware) is { } profile)
                hardware = profile.DisplayName;
        }
        catch (InvalidOperationException)
        {
            // The profile id is a fine name too.
        }
        var name = recipe.Variant is null or "default" ? hardware : $"{hardware} · {recipe.Variant}";
        var siblings = await _context.VllmRecipes.Where(r => r.VllmModelId == recipe.VllmModelId).Select(r => r.Name).ToListAsync(ct);
        return UniqueName(name, siblings);
    }

    private static bool SameRecipe(VllmRecipe saved, VllmRecipe input, Dictionary<string, string> settings) =>
        string.Equals(saved.ServedModel, input.ServedModel.Trim(), StringComparison.Ordinal)
        && string.Equals(saved.Hardware, Blank(input.Hardware), StringComparison.Ordinal)
        && saved.Settings.Count == settings.Count && !saved.Settings.Except(settings).Any();

    private static List<VllmRecipe> OrderRecipes(IEnumerable<VllmRecipe> recipes) =>
        recipes.OrderBy(r => r.Hardware ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static string UniqueName(string name, IReadOnlyCollection<string> taken)
    {
        var candidate = name;
        for (int i = 2; taken.Contains(candidate, StringComparer.OrdinalIgnoreCase); i++)
            candidate = string.Create(CultureInfo.InvariantCulture, $"{name} {i}");
        return candidate;
    }

    private static string? FeaturesText(IReadOnlyList<string>? features) => features is null ? null : string.Join(',', features);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex HfIdRegex();
}
