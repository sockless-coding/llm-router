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
/// Several router instances sharing one engine install root and model root: installs and model files
/// one instance makes are registered by the others, models are linked to their Hugging Face repo, and
/// entries removed while keeping their files stay removed.
/// </summary>
public sealed class SharedLibraryDiscoveryTests : IDisposable
{
    private static readonly string Exe = OperatingSystem.IsWindows() ? ".exe" : "";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lr-discovery-" + Guid.NewGuid().ToString("N"));
    private readonly string _engines;
    private readonly string _models;
    private readonly SqliteConnection _connection;
    private readonly LRDbContext _context;
    private readonly StubHub _hub = new();
    private readonly ModelLibraryManager _library;
    private readonly EngineBuildManager _engineManager;
    private readonly SharedLibraryDiscovery _discovery;

    public SharedLibraryDiscoveryTests()
    {
        _engines = Directory.CreateDirectory(Path.Combine(_root, "engines")).FullName;
        _models = Directory.CreateDirectory(Path.Combine(_root, "models")).FullName;

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new LRDbContext(new DbContextOptionsBuilder<LRDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        IEngineInstallHandler[] handlers = [new LlamaCppInstallHandler(), new SdCppInstallHandler(), new TensorFoldInstallHandler(), new VllmInstallHandler(), new StrataInstallHandler()];
        _library = new ModelLibraryManager(_context, new NoGguf());
        _engineManager = new EngineBuildManager(_context, new NoGitHub(), handlers);
        _discovery = new SharedLibraryDiscovery(
            _context, _library,
            new ModelSettings(new ModelLibrarySettings { RootFolder = _models }),
            new EngineSettings(new EngineBuildSettings { InstallRootFolder = _engines }),
            _engineManager, _hub, handlers, NullLogger<SharedLibraryDiscovery>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string LlamaInstall(string folderName)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_engines, folderName)).FullName;
        File.WriteAllText(Path.Combine(dir, "llama-server" + Exe), "");
        return dir;
    }

    private string ModelFile(string relative, int size = 100)
    {
        var path = Path.Combine(_models, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    // ==================== ENGINES ====================

    [Fact]
    public async Task AnInstallWithAManifest_IsRegisteredWithItsDetails()
    {
        var dir = LlamaInstall("b7000-cuda");
        var (os, arch) = ReleaseAssetResolver.DetectHost();
        new EngineInstallManifest
        {
            Name = "llama.cpp b7000 (Cuda)", Engine = ServerEngine.LlamaCpp, BackendType = BackendType.Cuda,
            Source = EngineBuildSource.OfficialRelease, VersionTag = "b7000", CommitSha = new string('a', 40),
            HostOs = os, HostArch = arch,
        }.TryWrite(dir);

        Assert.Equal(1, await _discovery.DiscoverEnginesAsync());

        var build = Assert.Single(await _context.EngineBuilds.ToListAsync());
        Assert.Equal("llama.cpp b7000 (Cuda)", build.Name);
        Assert.Equal(BackendType.Cuda, build.BackendType);
        Assert.Equal("b7000", build.VersionTag);
        Assert.Equal(new string('a', 40), build.CommitSha);
        Assert.Equal(EngineBuildStatus.Ready, build.Status);
        Assert.Equal(0, await _discovery.DiscoverEnginesAsync()); // already tracked
    }

    [Fact]
    public async Task AnInstallMadeOnAnotherKindOfMachine_IsLeftAlone()
    {
        var dir = LlamaInstall("b7000-metal");
        new EngineInstallManifest { Engine = ServerEngine.LlamaCpp, HostOs = "some-other-os", HostArch = "x64" }.TryWrite(dir);

        Assert.Equal(0, await _discovery.DiscoverEnginesAsync());
    }

    [Fact]
    public async Task AnOlderInstall_IsReadFromItsFolderName_AndGetsAManifest()
    {
        var dir = LlamaInstall("b6500-vulkan");

        Assert.Equal(1, await _discovery.DiscoverEnginesAsync());

        var build = Assert.Single(await _context.EngineBuilds.ToListAsync());
        Assert.Equal(EngineBuildSource.OfficialRelease, build.Source);
        Assert.Equal(BackendType.Vulkan, build.BackendType);
        Assert.Equal("b6500", build.VersionTag);
        Assert.NotNull(EngineInstallManifest.TryRead(dir));
    }

    [Fact]
    public async Task TheWorkspaceAndDockerFolders_AreSkipped()
    {
        LlamaInstall(".build");
        Directory.CreateDirectory(Path.Combine(_engines, "tensorfold-v1.0-xpu-docker"));
        File.WriteAllText(Path.Combine(_engines, "tensorfold-v1.0-xpu-docker", "Dockerfile"), "");

        Assert.Equal(0, await _discovery.DiscoverEnginesAsync());
    }

    [Fact]
    public async Task AnInPlaceUpdateByAnotherInstance_IsPickedUpFromTheManifest()
    {
        var dir = LlamaInstall("b6500-cuda");
        await _discovery.DiscoverEnginesAsync();
        var build = await _context.EngineBuilds.SingleAsync();

        var manifest = EngineInstallManifest.TryRead(dir)!;
        manifest.VersionTag = "b7100";
        manifest.CommitSha = new string('b', 40);
        manifest.TryWrite(dir);
        await _engineManager.ReconcileAsync();

        await _context.Entry(build).ReloadAsync();
        Assert.Equal("b7100", build.VersionTag);
        Assert.Equal(new string('b', 40), build.CommitSha);
    }

    [Fact]
    public async Task ARemovedInstallWithFilesKept_StaysRemoved_UntilItsFolderIsGone()
    {
        var dir = LlamaInstall("b6500-cpu");
        await _discovery.DiscoverEnginesAsync();
        var build = await _context.EngineBuilds.SingleAsync();

        await _engineManager.DeleteBuildAsync(build.Id, deleteFiles: false);
        Assert.Equal(0, (await _discovery.RunAsync()).EnginesAdded);

        Directory.Delete(dir, recursive: true);
        await _discovery.RunAsync();
        Assert.Empty(await _context.DiscoveryExclusions.ToListAsync());
    }

    [Theory]
    [InlineData(ServerEngine.LlamaCpp, "b6500-cuda", "b6500", BackendType.Cuda, EngineBuildSource.OfficialRelease, null)]
    [InlineData(ServerEngine.LlamaCpp, "master-sycl-sycl-fp32", null, BackendType.Sycl, EngineBuildSource.SourceCompile, "sycl-fp32")]
    [InlineData(ServerEngine.StableDiffusionCpp, "sd-master-504-636d3cb-vulkan", "master-504-636d3cb", BackendType.Vulkan, EngineBuildSource.OfficialRelease, null)]
    [InlineData(ServerEngine.TensorFold, "tensorfold-v0.4.2-cuda", "v0.4.2", BackendType.Cuda, EngineBuildSource.OfficialRelease, null)]
    [InlineData(ServerEngine.Vllm, "vllm-v0.20.1-cuda", "v0.20.1", BackendType.Cuda, EngineBuildSource.OfficialRelease, null)]
    [InlineData(ServerEngine.LlamaCpp, "my-llama", null, BackendType.Unknown, EngineBuildSource.OfficialRelease, null)]
    public void FolderNames_AreParsedTheWayTheRouterWritesThem(
        ServerEngine engine, string folder, string? tag, BackendType backend, EngineBuildSource source, string? recipe)
    {
        var info = EngineFolderInfo.Parse(engine, folder);
        Assert.Equal(tag, info.VersionTag);
        Assert.Equal(backend, info.Backend);
        Assert.Equal(source, info.Source);
        Assert.Equal(recipe, info.RecipeSlug);
    }

    // ==================== MODELS ====================

    [Fact]
    public async Task NewModelFiles_AreRegistered_ButNotHalfDownloadedOnes()
    {
        ModelFile("bartowski_Qwen3-8B-GGUF/Qwen3-8B-Q4_K_M.gguf");
        ModelFile("big_Model-GGUF/Model-00001-of-00002.gguf"); // second shard still downloading
        ModelFile("big_Model-GGUF/Model-00002-of-00002.gguf.part");

        var (added, _, _) = await _discovery.DiscoverModelsAsync();

        Assert.Equal(1, added);
        Assert.Equal("Qwen3-8B-Q4_K_M", (await _context.LocalModels.SingleAsync()).Name);
    }

    [Fact]
    public async Task AFileWithAManifest_IsLinkedToItsRepo()
    {
        var path = ModelFile("unsloth_Qwen3-8B-GGUF/Q4_K_M/Qwen3-8B-Q4_K_M.gguf");
        Assert.True(HfSourceManifest.RecordFile(path, "unsloth/Qwen3-8B-GGUF", "Q4_K_M/Qwen3-8B-Q4_K_M.gguf", "abc123"));

        await _discovery.DiscoverModelsAsync();
        Assert.Equal(1, await _discovery.LinkHuggingFaceAsync());

        var model = await _context.LocalModels.SingleAsync();
        Assert.Equal(ModelSource.HuggingFace, model.Source);
        Assert.Equal("unsloth/Qwen3-8B-GGUF", model.HfRepoId);
        Assert.Equal("Q4_K_M/Qwen3-8B-Q4_K_M.gguf", model.HfFilename);
        Assert.Equal("abc123", model.HfRevision);
        Assert.Equal(0, _hub.Calls); // no need to ask the Hub
    }

    [Fact]
    public async Task AnUpdateByAnotherInstance_MovesTheRevision()
    {
        var path = ModelFile("unsloth_Qwen3-8B-GGUF/Qwen3-8B-Q4_K_M.gguf");
        HfSourceManifest.RecordFile(path, "unsloth/Qwen3-8B-GGUF", "Qwen3-8B-Q4_K_M.gguf", "old");
        await _discovery.DiscoverModelsAsync();
        await _discovery.LinkHuggingFaceAsync();

        HfSourceManifest.RecordFile(path, "unsloth/Qwen3-8B-GGUF", "Qwen3-8B-Q4_K_M.gguf", "new");
        Assert.Equal(1, await _discovery.LinkHuggingFaceAsync());
        Assert.Equal("new", (await _context.LocalModels.SingleAsync()).HfRevision);
    }

    [Fact]
    public async Task AFileWithoutAManifest_IsMatchedAgainstTheHub_WithAnUnderscoreInTheRepoName()
    {
        var path = ModelFile("some-org_My_Model-GGUF/My_Model-Q8_0.gguf", size: 4096);
        _hub.Repos["some-org/My_Model-GGUF"] = new HfRepoDetail
        {
            Id = "some-org/My_Model-GGUF",
            Sha = "deadbeef",
            Siblings = [new HfRepoFile { Filename = "My_Model-Q8_0.gguf", SizeBytes = 4096 }],
        };

        await _discovery.DiscoverModelsAsync();
        Assert.Equal(1, await _discovery.LinkHuggingFaceAsync());

        var model = await _context.LocalModels.SingleAsync();
        Assert.Equal("some-org/My_Model-GGUF", model.HfRepoId);
        Assert.Equal("My_Model-Q8_0.gguf", model.HfFilename);
        Assert.Equal("deadbeef", model.HfRevision); // same size as the repo's file
        Assert.NotNull(HfSourceManifest.TryRead(Path.GetDirectoryName(path)!)); // so others needn't ask
    }

    [Fact]
    public async Task AFileOfADifferentSizeThanTheRepos_IsLinked_WithAnUnknownRevision()
    {
        ModelFile("org_Model-GGUF/Model-Q4.gguf", size: 10);
        _hub.Repos["org/Model-GGUF"] = new HfRepoDetail
        {
            Id = "org/Model-GGUF", Sha = "newer",
            Siblings = [new HfRepoFile { Filename = "Model-Q4.gguf", SizeBytes = 20 }],
        };

        await _discovery.DiscoverModelsAsync();
        await _discovery.LinkHuggingFaceAsync();

        var model = await _context.LocalModels.SingleAsync();
        Assert.Equal("org/Model-GGUF", model.HfRepoId);
        Assert.Null(model.HfRevision); // the update check offers the repo's current version
    }

    [Fact]
    public async Task AFileTheHubDoesntKnow_StaysLocal_AndIsntAskedAboutEveryPass()
    {
        ModelFile("my_stuff/finetune.gguf");

        await _discovery.DiscoverModelsAsync();
        Assert.Equal(0, await _discovery.LinkHuggingFaceAsync());
        var calls = _hub.Calls;
        await _discovery.LinkHuggingFaceAsync();

        Assert.Equal(ModelSource.Local, (await _context.LocalModels.SingleAsync()).Source);
        Assert.Equal(calls, _hub.Calls);
    }

    [Fact]
    public async Task ARemovedModelWithItsFileKept_IsntAddedAgain_UntilImportedByHand()
    {
        var path = ModelFile("org_Model-GGUF/Model-Q4.gguf");
        await _discovery.DiscoverModelsAsync();
        var model = await _context.LocalModels.SingleAsync();

        await _library.DeleteAsync(model.Id, deleteFile: false);
        Assert.Equal(0, (await _discovery.DiscoverModelsAsync()).Added);

        await _library.ImportFromPathAsync(path);
        Assert.Empty(await _context.DiscoveryExclusions.ToListAsync());
    }

    [Fact]
    public async Task AModelDeletedByAnotherInstance_IsMarkedMissing()
    {
        var path = ModelFile("org_Model-GGUF/Model-Q4.gguf");
        await _discovery.DiscoverModelsAsync();

        File.Delete(path);
        var (_, missing, _) = await _discovery.DiscoverModelsAsync();

        Assert.Equal(1, missing);
        Assert.Equal(ModelStatus.Missing, (await _context.LocalModels.SingleAsync()).Status);
    }

    [Fact]
    public void HubCachePaths_GiveRepoRevisionAndFile()
    {
        var sha = new string('c', 40);
        var path = Path.Combine(_root, "hub", "models--Qwen--Qwen3-8B", "snapshots", sha, "sub", "model.safetensors");

        var match = HfPathInference.FromHubCache(path);

        Assert.NotNull(match);
        Assert.Equal("Qwen/Qwen3-8B", match.RepoId);
        Assert.Equal(sha, match.Revision);
        Assert.Equal("sub/model.safetensors", match.Filename);
        Assert.Null(HfPathInference.FromHubCache(Path.Combine(_root, "models", "file.gguf")));
    }

    [Fact]
    public void RouterLayout_OffersEverySplitOfTheFolderName()
    {
        var candidates = HfPathInference.FromRouterLayout(_models, Path.Combine(_models, "a_b_c", "x", "f.gguf"), checkpoint: false);
        Assert.Equal(["a/b_c", "a_b/c"], candidates.Select(c => c.RepoId));
        Assert.All(candidates, c => Assert.Equal("x/f.gguf", c.Filename));

        var checkpoint = HfPathInference.FromRouterLayout(_models, Path.Combine(_models, "checkpoints", "Qwen_Qwen3-8B"), checkpoint: true);
        Assert.Equal("Qwen/Qwen3-8B", Assert.Single(checkpoint).RepoId);
    }

    // ==================== STUBS ====================

    private sealed class NoGguf : IGgufMetadataReader
    {
        public Task<GgufMetadata?> ReadAsync(string filePath, CancellationToken ct = default) => Task.FromResult<GgufMetadata?>(null);
    }

    private sealed class ModelSettings(ModelLibrarySettings settings) : IModelLibrarySettingsService
    {
        public Task<ModelLibrarySettings> GetAsync(CancellationToken ct = default) => Task.FromResult(settings);
        public Task SaveAsync(string rootFolder, string? huggingFaceApiToken, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class EngineSettings(EngineBuildSettings settings) : IEngineBuildSettingsService
    {
        public Task<EngineBuildSettings> GetAsync(CancellationToken ct = default) => Task.FromResult(settings);
        public Task SaveAsync(string installRootFolder, string? buildWorkspaceFolder, string? gitHubApiToken, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubHub : IHuggingFaceClient
    {
        public Dictionary<string, HfRepoDetail> Repos { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Calls { get; private set; }

        public Task<HfRepoDetail?> GetRepoDetailAsync(string repoId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Repos.GetValueOrDefault(repoId));
        }

        public Task<IReadOnlyList<HfModelSummary>> SearchModelsAsync(string query, HfModelFormat format, string? pipelineTag = null, int limit = 20, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HfModelSummary>>([]);
        public Task<IReadOnlyList<HfRepoFile>> ListModelFilesAsync(string repoId, HfModelFormat format, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HfRepoFile>>([]);
        public Task<string?> GetFileTextAsync(string repoId, string filename, string revision = "main", CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> DownloadFileAsync(string repoId, string filename, string revision, string destinationPath, IProgress<DownloadProgress>? progress, CancellationToken ct = default) =>
            throw new NotSupportedException();
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
