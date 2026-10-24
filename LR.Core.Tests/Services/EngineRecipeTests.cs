using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using LR.Core.Data;
using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Tests.Services;

/// <summary>
/// Compile recipes across engines: built-in templates for llama.cpp and stable-diffusion.cpp share
/// names without colliding, recipes are listed per engine, sd.cpp's upstream build docs are read
/// with its own <c>SD_*</c> flags, and source-build version tags follow each engine's tag scheme.
/// </summary>
public sealed class EngineRecipeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LRDbContext _context;
    private readonly EngineBuildManager _manager;

    public EngineRecipeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new LRDbContext(new DbContextOptionsBuilder<LRDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _manager = new EngineBuildManager(_context, new NoGitHub(), [new LlamaCppInstallHandler(), new SdCppInstallHandler()]);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Seed_AddsBuiltInsPerEngine_AndIsIdempotent()
    {
        await _manager.SeedBuiltInRecipesAsync();
        await _manager.SeedBuiltInRecipesAsync();

        var llama = await _manager.GetRecipesAsync(ServerEngine.LlamaCpp);
        var sd = await _manager.GetRecipesAsync(ServerEngine.StableDiffusionCpp);

        Assert.Equal(BuiltInRecipeTemplates.All().Count(), (await _manager.GetRecipesAsync()).Count);
        Assert.Contains(llama, r => r.Name == "SYCL FP32");
        Assert.Contains(sd, r => r.Name == "SYCL FP32");
        Assert.All(sd, r => Assert.Equal(LlamaCppBuildRecipe.DefaultRepoUrl(ServerEngine.StableDiffusionCpp), r.GitRepoUrl));
    }

    [Fact]
    public void SdCppSyclTemplates_UseSdSwitchAndOneApi()
    {
        var sycl = BuiltInRecipeTemplates.All()
            .Where(r => r.Engine == ServerEngine.StableDiffusionCpp && r.BackendType == BackendType.Sycl)
            .ToList();

        Assert.Equal(2, sycl.Count);
        Assert.All(sycl, r =>
        {
            Assert.Contains("-DSD_SYCL=ON", r.CMakeArgs);
            Assert.DoesNotContain(r.CMakeArgs, a => a.StartsWith("-DLLAMA_"));
            Assert.Contains("setvars", r.EnvironmentSetupCommand);
        });
        Assert.Contains(sycl, r => r.CMakeArgs.Contains("-DGGML_SYCL_F16=ON"));
    }

    [Fact]
    public void BuiltInNames_AreUniquePerEngine()
    {
        var dupes = BuiltInRecipeTemplates.All()
            .GroupBy(r => (r.Engine, r.Name.ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        Assert.Empty(dupes);
    }

    [Fact]
    public void UpstreamDocs_PicksSdCppCommandsForBackend()
    {
        const string buildMd = """
            ```shell
            cmake .. -DSD_CUDA=ON
            ```
            ```shell
            source /opt/intel/oneapi/setvars.sh
            cmake .. -DSD_SYCL=ON -DCMAKE_C_COMPILER=icx -DCMAKE_CXX_COMPILER=icpx -DGGML_SYCL_F16=ON
            ```
            ```shell
            cmake ..
            ```
            """;

        var sycl = UpstreamBuildDocs.ExtractCommandBlocks("docs/build.md", buildMd, BackendType.Sycl);
        var block = Assert.Single(sycl);
        Assert.Equal(["-DSD_SYCL=ON", "-DCMAKE_C_COMPILER=icx", "-DCMAKE_CXX_COMPILER=icpx", "-DGGML_SYCL_F16=ON"], block.Parsed!.CMakeArgs);

        // CPU skips commands that turn on an sd.cpp backend.
        Assert.DoesNotContain(UpstreamBuildDocs.ExtractCommandBlocks("docs/build.md", buildMd, BackendType.Cpu),
            c => c.Command.Contains("SD_CUDA"));

        Assert.Equal(["docs/build.md"], UpstreamBuildDocs.SourcesFor(ServerEngine.StableDiffusionCpp, BackendType.Sycl));
    }

    [Fact]
    public void VersionTags_FollowEachEnginesScheme()
    {
        // A sha that starts with "b" must not be mistaken for a llama.cpp build number.
        Assert.Equal("master-512-b1c2d3e", SdCppReleaseProfile.Instance.VersionTagOf("master-512-b1c2d3e"));
        Assert.Equal("b6512", LlamaCppReleaseProfile.Instance.VersionTagOf("b6512-3-gdeadbee"));
    }

    private sealed class NoGitHub : IGitHubClient
    {
        public Task<GitHubCompareResult?> CompareAsync(string repo, string baseRef, string headRef, CancellationToken ct = default) => Task.FromResult<GitHubCompareResult?>(null);
        public Task<GitHubRelease?> GetLatestReleaseAsync(string repo, CancellationToken ct = default) => Task.FromResult<GitHubRelease?>(null);
        public Task<GitHubRelease?> GetReleaseByTagAsync(string repo, string tag, CancellationToken ct = default) => Task.FromResult<GitHubRelease?>(null);
        public Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string repo, int limit = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GitHubRelease>>([]);
        public Task<string?> GetRawFileAsync(string repo, string reference, string path, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DownloadAssetAsync(string downloadUrl, string destinationPath, IProgress<EngineBuildProgress>? progress, CancellationToken ct = default) => Task.CompletedTask;
    }
}
