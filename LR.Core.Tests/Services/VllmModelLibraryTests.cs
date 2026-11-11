using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Tests.Services;

/// <summary>
/// The vLLM model library: models added from recipes.vllm.ai or the Hub, site recipes saved per
/// hardware choice, hand edits turning a site recipe custom, and what a preset gets from a recipe.
/// </summary>
public sealed class VllmModelLibraryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LRDbContext _context;
    private readonly StubRecipes _recipes = new();
    private readonly StubHub _hub = new();
    private readonly VllmModelLibrary _library;

    public VllmModelLibraryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new LRDbContext(new DbContextOptionsBuilder<LRDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _library = new VllmModelLibrary(_context, _recipes, _hub, NullLogger<VllmModelLibrary>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task AddModel_FillsInFromTheSiteAndHub_UsesTheHubsSpelling_AndIsIdempotent()
    {
        _hub.Repos["Qwen/Qwen3-8B"] = new HfRepoDetail { Id = "Qwen/Qwen3-8B", Author = "Qwen", PipelineTag = "text-generation" };

        var model = await _library.AddModelAsync("qwen/qwen3-8b");
        var again = await _library.AddModelAsync("Qwen/Qwen3-8B");

        Assert.Equal("Qwen/Qwen3-8B", model.HfId);
        Assert.Equal("Qwen3-8B", model.Name);
        Assert.Equal("Qwen", model.Provider);
        Assert.Equal(40960, model.ContextLength);
        Assert.Equal("text-generation", model.PipelineTag);
        Assert.True(model.HasSiteRecipe);
        Assert.Equal(model.Id, again.Id);
        Assert.Single(await _library.GetModelsAsync());
    }

    [Fact]
    public async Task AddModel_WithoutARecipe_ComesFromTheHub()
    {
        _hub.Repos["org/custom"] = new HfRepoDetail { Id = "org/custom", Author = "org" };

        var model = await _library.AddModelAsync("org/custom");

        Assert.False(model.HasSiteRecipe);
        Assert.Equal("custom", model.Name);
    }

    [Theory]
    [InlineData("nobody/knows")]
    [InlineData("not an id")]
    [InlineData("../x")]
    public async Task AddModel_RejectsWhatNeitherSourceKnows(string id)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _library.AddModelAsync(id));
        Assert.Empty(await _library.GetModelsAsync());
    }

    [Fact]
    public async Task SiteRecipe_StoresTheConvertedCommand_NamedAfterTheHardware_AndRefreshes()
    {
        var model = await _library.AddModelAsync("Qwen/Qwen3-8B");

        var recipe = await _library.AddSiteRecipeAsync(model.Id, new VllmRecipeSelection("rtx_5090"), null);
        var second = await _library.AddSiteRecipeAsync(model.Id, new VllmRecipeSelection("rtx_5090"), null);

        Assert.Equal(VllmRecipeOrigin.Site, recipe.Origin);
        Assert.Equal("RTX 5090", recipe.Name);
        Assert.Equal("RTX 5090 2", second.Name);
        Assert.Equal("rtx_5090", recipe.Hardware);
        Assert.Equal("Qwen/Qwen3-8B", recipe.ServedModel);
        Assert.Equal("hermes", recipe.Settings[VllmPresetSettings.ToolCallParser]);
        Assert.Equal(20, recipe.VramGb);
        Assert.Contains("20 GB", recipe.Warnings);
        Assert.NotNull(recipe.FetchedAt);
        Assert.Equal("https://recipes.vllm.ai/Qwen/Qwen3-8B?hardware=rtx_5090", recipe.PageUrl);

        _recipes.ToolParser = "qwen3_xml";
        var refreshed = await _library.RefreshSiteRecipeAsync(recipe.Id);
        Assert.Equal("qwen3_xml", refreshed.Settings[VllmPresetSettings.ToolCallParser]);
        Assert.Equal(["rtx_5090", "rtx_5090", "rtx_5090"], _recipes.RenderedHardware);
    }

    [Fact]
    public async Task EditingASiteRecipe_MakesItCustom_RenamingDoesNot()
    {
        var model = await _library.AddModelAsync("Qwen/Qwen3-8B");
        var recipe = await _library.AddSiteRecipeAsync(model.Id, new VllmRecipeSelection("rtx_5090"), "Mine");

        var renamed = await _library.SaveRecipeAsync(Copy(recipe, name: "Renamed"));
        Assert.Equal(VllmRecipeOrigin.Site, renamed.Origin);

        var settings = new Dictionary<string, string>(recipe.Settings) { [VllmPresetSettings.MaxModelLen] = "16k" };
        var edited = await _library.SaveRecipeAsync(Copy(recipe, settings: settings));
        Assert.Equal(VllmRecipeOrigin.Custom, edited.Origin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _library.RefreshSiteRecipeAsync(recipe.Id));
    }

    [Fact]
    public async Task CustomRecipe_IsValidatedLikeAPreset()
    {
        var model = await _library.AddModelAsync("Qwen/Qwen3-8B");
        var bad = new VllmRecipe { VllmModelId = model.Id, Name = "Bad", ServedModel = model.HfId, Settings = new() { [VllmPresetSettings.ExtraArgs] = "--port\n9000" } };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _library.SaveRecipeAsync(bad));
        Assert.Contains("--port", error.Message);

        var good = new VllmRecipe
        {
            VllmModelId = model.Id, Name = "B70", Hardware = "arc_pro_b70", ServedModel = "org/Qwen3.8-27B-GPTQ",
            Settings = new(VllmDocker.Qwen38Preset) { [VllmPresetSettings.Dtype] = "  " },
        };
        var saved = await _library.SaveRecipeAsync(good);
        Assert.Equal(VllmRecipeOrigin.Custom, saved.Origin);
        Assert.False(saved.Settings.ContainsKey(VllmPresetSettings.Dtype));
    }

    [Fact]
    public async Task Duplicate_IsCustom_AndDeletingTheModelRemovesItsRecipes()
    {
        var model = await _library.AddModelAsync("Qwen/Qwen3-8B");
        var recipe = await _library.AddSiteRecipeAsync(model.Id, new VllmRecipeSelection("rtx_5090"), "Fast");

        var copy = await _library.DuplicateRecipeAsync(recipe.Id);

        Assert.NotNull(copy);
        Assert.Equal("Fast (copy)", copy.Name);
        Assert.Equal(VllmRecipeOrigin.Custom, copy.Origin);
        Assert.Equal(recipe.Settings, copy.Settings);

        Assert.True(await _library.DeleteModelAsync(model.Id));
        Assert.Empty(_context.VllmRecipes);
    }

    [Fact]
    public async Task ForPreset_AddsWhichRecipeItWas()
    {
        var model = await _library.AddModelAsync("Qwen/Qwen3-8B");
        var recipe = await _library.SaveRecipeAsync(new VllmRecipe
        {
            VllmModelId = model.Id, Name = "Custom", Hardware = "arc_pro_b70", ServedModel = model.HfId, VramGb = 18.5,
            Settings = new() { [VllmPresetSettings.MaxModelLen] = "32k" },
        });

        var settings = VllmPresetSettings.ForPreset(recipe);

        Assert.Equal(recipe.Id.ToString(), settings[VllmPresetSettings.SavedRecipeId]);
        Assert.Equal("Custom", settings[VllmPresetSettings.SavedRecipeName]);
        Assert.Equal("arc_pro_b70", settings[VllmPresetSettings.RecipeHardware]);
        Assert.Equal("18.5", settings[VllmPresetSettings.VramGb]);
        Assert.Equal("32k", settings[VllmPresetSettings.MaxModelLen]);
    }

    private static VllmRecipe Copy(VllmRecipe r, string? name = null, Dictionary<string, string>? settings = null) => new()
    {
        Id = r.Id, VllmModelId = r.VllmModelId, Name = name ?? r.Name, Hardware = r.Hardware, ServedModel = r.ServedModel,
        Settings = settings ?? new(r.Settings), Warnings = r.Warnings, VramGb = r.VramGb, Notes = r.Notes,
    };

    private sealed class StubRecipes : IVllmRecipeClient
    {
        public string ToolParser { get; set; } = "hermes";
        public List<string?> RenderedHardware { get; } = [];

        public Task<VllmRecipeCatalog> GetCatalogAsync(CancellationToken ct = default) => Task.FromResult(new VllmRecipeCatalog(
            [new VllmRecipeSummary("Qwen/Qwen3-8B", "Qwen3-8B", "Qwen")],
            [new VllmHardwareProfile("rtx_5090", "RTX 5090", "NVIDIA", 1, 32)],
            [new VllmStrategy("single_node_tp", "Tensor Parallel", null)]));

        public Task<VllmRecipeDetails?> GetRecipeAsync(string hfId, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(hfId, "Qwen/Qwen3-8B", StringComparison.OrdinalIgnoreCase)
                ? new VllmRecipeDetails("Qwen/Qwen3-8B", "Qwen3-8B", "Small and quick.", "0.10.0", 40960, ["text"], "rtx_5090",
                    [new VllmRecipeVariant("default", null, "bf16", 20, null, null, [])], [], null, ["rtx_5090"])
                : null);

        public Task<VllmRecipeRendering?> RenderAsync(string hfId, VllmRecipeSelection selection, CancellationToken ct = default)
        {
            RenderedHardware.Add(selection.Hardware);
            return Task.FromResult<VllmRecipeRendering?>(new VllmRecipeRendering(hfId, selection.Hardware ?? "rtx_5090", "single_node_tp", "default", "single_node",
                ["vllm", "serve", "Qwen/Qwen3-8B", "--enable-auto-tool-choice", "--tool-call-parser", ToolParser, "--port", "8000"],
                new Dictionary<string, string>(), [], $"https://recipes.vllm.ai/Qwen/Qwen3-8B?hardware={selection.Hardware}"));
        }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetHardwareIndexAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(new Dictionary<string, IReadOnlyList<string>> { ["Qwen/Qwen3-8B"] = ["rtx_5090"] });
    }

    private sealed class StubHub : IHuggingFaceClient
    {
        public Dictionary<string, HfRepoDetail> Repos { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<HfRepoDetail?> GetRepoDetailAsync(string repoId, CancellationToken ct = default) =>
            Task.FromResult(Repos.GetValueOrDefault(repoId));

        public Task<IReadOnlyList<HfModelSummary>> SearchModelsAsync(string query, HfModelFormat format, string? pipelineTag = null, int limit = 20, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HfModelSummary>>([]);

        public Task<IReadOnlyList<HfRepoFile>> ListModelFilesAsync(string repoId, HfModelFormat format, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HfRepoFile>>([]);

        public Task<string?> DownloadFileAsync(string repoId, string filename, string revision, string destinationPath, IProgress<DownloadProgress>? progress, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}

public class VllmHardwareMatcherTests
{
    private static readonly VllmHardwareProfile[] Profiles =
    [
        new("rtx_5090", "RTX 5090", "NVIDIA", 1, 32),
        new("rtx_5090_2x", "2× RTX 5090", "NVIDIA", 2, 64),
        new("arc_pro_b70", "Arc Pro B70", "Intel", 4, 128),
        new("arc_pro_b60", "Arc Pro B60", "Intel", 4, 96),
        new("h100", "H100", "NVIDIA", 8, 640),
    ];

    private static ComputeDevice Gpu(string name) => new() { InstanceId = name, Name = name, Kind = ComputeDeviceKind.Gpu };

    [Theory]
    [InlineData("Intel(R) Arc(TM) Pro B70 Graphics", "arc_pro_b70")]
    [InlineData("NVIDIA GeForce RTX 5090", "rtx_5090")]
    public void MatchesDeviceNamesToProfiles(string device, string expected)
    {
        Assert.Equal(expected, VllmHardwareMatcher.Match([Gpu(device)], Profiles).First().Id);
    }

    [Fact]
    public void NoGpus_OrUnknownOnes_MatchNothing()
    {
        Assert.Empty(VllmHardwareMatcher.Match([], Profiles));
        Assert.Empty(VllmHardwareMatcher.Match([Gpu("Microsoft Basic Display Adapter")], Profiles));
        Assert.Empty(VllmHardwareMatcher.Match([new ComputeDevice { InstanceId = "npu", Name = "RTX 5090", Kind = ComputeDeviceKind.Npu }], Profiles));
    }
}
