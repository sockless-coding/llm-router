using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.Engines;

public class EngineRecipeModel : PageModel
{
    private readonly IEngineBuildManager _manager;

    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    /// <summary>When set, pre-fill the form from an existing recipe as a starting point for a new one.</summary>
    [BindProperty(SupportsGet = true)]
    public Guid CloneFrom { get; set; }

    [BindProperty] public RecipeInput Input { get; set; } = new();

    public bool IsExisting { get; set; }
    public bool IsBuiltIn { get; set; }
    public string? StatusMessage { get; set; }
    public IReadOnlyList<LlamaCppBuildRecipe> AllRecipes { get; set; } = new List<LlamaCppBuildRecipe>();

    public EngineRecipeModel(IEngineBuildManager manager) => _manager = manager;

    public async Task<IActionResult> OnGetAsync()
    {
        AllRecipes = await _manager.GetRecipesAsync();

        var source = Id != Guid.Empty ? await _manager.GetRecipeAsync(Id)
            : CloneFrom != Guid.Empty ? await _manager.GetRecipeAsync(CloneFrom)
            : null;

        if (Id != Guid.Empty)
        {
            if (source is null) return NotFound();
            IsExisting = true;
            IsBuiltIn = source.IsBuiltIn;
        }

        if (source is not null)
        {
            Input = RecipeInput.From(source);
            if (CloneFrom != Guid.Empty)
            {
                Input.Name = $"{source.Name} (copy)";
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        AllRecipes = await _manager.GetRecipesAsync();

        if (string.IsNullOrWhiteSpace(Input.Name))
        {
            StatusMessage = "Name is required.";
            return Page();
        }

        var recipe = Input.ToRecipe(Id != Guid.Empty && !IsBuiltIn ? Id : Guid.Empty);

        // Built-in recipes are read-only — editing one saves a user copy instead.
        var existing = Id != Guid.Empty ? await _manager.GetRecipeAsync(Id) : null;
        if (existing?.IsBuiltIn == true)
            recipe.Id = Guid.Empty;

        var saved = await _manager.SaveRecipeAsync(recipe);
        return RedirectToPage("Recipe", new { id = saved.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        await _manager.DeleteRecipeAsync(Id);
        return RedirectToPage("Index");
    }

    /// <summary>
    /// Fetches llama.cpp's own build docs / example scripts for a backend and returns the candidate
    /// cmake commands (with a best-effort parse). Called from the recipe editor's reference panel.
    /// </summary>
    public async Task<IActionResult> OnGetReferenceAsync(BackendType backend, CancellationToken ct)
    {
        var docs = await _manager.GetUpstreamReferenceAsync(backend, ct);
        return new JsonResult(new
        {
            docs = docs.Select(d => new
            {
                path = d.Path,
                url = d.Url,
                error = d.Error,
                commands = d.Commands.Select(c => new
                {
                    command = c.Command,
                    parsed = c.Parsed is null ? null : new
                    {
                        cmakeArgs = c.Parsed.CMakeArgs,
                        generator = c.Parsed.Generator,
                        buildConfig = c.Parsed.BuildConfig,
                        ignored = c.Parsed.Ignored,
                    },
                }),
            }),
        });
    }

    public class RecipeInput
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public BackendType BackendType { get; set; } = BackendType.Cpu;
        public string GitRepoUrl { get; set; } = "https://github.com/ggml-org/llama.cpp";
        public string GitRef { get; set; } = "master";
        public string CMakeArgsText { get; set; } = "";
        public string? CMakeGenerator { get; set; }
        public string BuildConfig { get; set; } = "Release";
        public string? EnvironmentSetupCommand { get; set; }
        public string ExtraArtifactGlobsText { get; set; } = "";

        public static RecipeInput From(LlamaCppBuildRecipe r) => new()
        {
            Name = r.Name,
            Description = r.Description,
            BackendType = r.BackendType,
            GitRepoUrl = r.GitRepoUrl,
            GitRef = r.GitRef,
            CMakeArgsText = string.Join('\n', r.CMakeArgs),
            CMakeGenerator = r.CMakeGenerator,
            BuildConfig = r.BuildConfig,
            EnvironmentSetupCommand = r.EnvironmentSetupCommand,
            ExtraArtifactGlobsText = string.Join('\n', r.ExtraArtifactGlobs),
        };

        public LlamaCppBuildRecipe ToRecipe(Guid id) => new()
        {
            Id = id,
            Name = Name.Trim(),
            Description = Description?.Trim(),
            BackendType = BackendType,
            GitRepoUrl = string.IsNullOrWhiteSpace(GitRepoUrl) ? "https://github.com/ggml-org/llama.cpp" : GitRepoUrl.Trim(),
            GitRef = string.IsNullOrWhiteSpace(GitRef) ? "master" : GitRef.Trim(),
            CMakeArgs = SplitLines(CMakeArgsText),
            CMakeGenerator = string.IsNullOrWhiteSpace(CMakeGenerator) ? null : CMakeGenerator.Trim(),
            BuildConfig = string.IsNullOrWhiteSpace(BuildConfig) ? "Release" : BuildConfig.Trim(),
            EnvironmentSetupCommand = string.IsNullOrWhiteSpace(EnvironmentSetupCommand) ? null : EnvironmentSetupCommand.Trim(),
            ExtraArtifactGlobs = SplitLines(ExtraArtifactGlobsText),
        };

        private static List<string> SplitLines(string text) => text
            .Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
