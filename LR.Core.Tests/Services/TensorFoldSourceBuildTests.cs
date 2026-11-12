using Microsoft.Extensions.Logging.Abstractions;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Tests.Services;

public sealed class TensorFoldSourceBuildTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tf-source-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FakeTools : IToolLocator
    {
        public string? NvccPath { get; init; } = "/cuda/bin/nvcc";
        public string? HipccPath { get; init; } = "/rocm/bin/hipcc";
        public string? OclocPath { get; init; } = "/usr/bin/ocloc";
        public HashSet<string> Path { get; init; } = ["c++"];

        public string? Nvcc() => NvccPath;
        public string? Hipcc() => HipccPath;
        public string? Ocloc() => OclocPath;
        public bool OnPath(string tool) => Path.Contains(tool);
    }

    [Fact]
    public void SourcePipeline_IsZigForTensorFold_AndCMakeForTheGgmlEngines()
    {
        Assert.Equal(
            [typeof(CheckTensorFoldToolsStep), typeof(GitSyncStep), typeof(EnsureZigStep), typeof(FetchTensorFoldDepsStep), typeof(ZigBuildStep), typeof(CollectZigOutputStep), typeof(FinalizeBuildStep)],
            TensorFoldSourceBuild.SourcePipeline(ServerEngine.TensorFold).Select(s => s.GetType()));
        Assert.Contains(TensorFoldSourceBuild.SourcePipeline(ServerEngine.LlamaCpp), s => s is CMakeConfigureStep);
    }

    [Theory]
    [InlineData(BackendType.Cuda, "native")]
    [InlineData(BackendType.Hip, "native-hip")]
    [InlineData(BackendType.Xpu, "xpu")]
    public void OutputVariant_FollowsTheBackend(BackendType backend, string variant)
    {
        Assert.Equal(variant, TensorFoldSourceBuild.OutputVariant(backend));
    }

    [Fact]
    public void Cuda_AddsTheInstalledNvcc_UnlessTheRecipeNamesOne()
    {
        Assert.Equal(["-Dgpu=sm_120", "-Dnvcc=/cuda/bin/nvcc"],
            TensorFoldSourceBuild.CompleteArgs(["-Dgpu=sm_120", " "], BackendType.Cuda, new FakeTools(), null, windows: true));
        Assert.Equal(["-Dgpu=sm_120", "-Dfatbins=/prebuilt"],
            TensorFoldSourceBuild.CompleteArgs(["-Dgpu=sm_120", "-Dfatbins=/prebuilt"], BackendType.Cuda, new FakeTools { NvccPath = null }, null, windows: false));
    }

    [Fact]
    public void Cuda_WithoutTheToolkit_SaysWhatToInstall()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TensorFoldSourceBuild.CompleteArgs(["-Dgpu=sm_89"], BackendType.Cuda, new FakeTools { NvccPath = null }, null, windows: true));
        Assert.Contains("CUDA Toolkit", ex.Message);
    }

    [Fact]
    public void Hip_NamesAHipccThatIsntOnPath()
    {
        string[] recipe = ["native-hip", "-Dhip=true", "-Dhip-arch=gfx1151"];

        Assert.Equal([.. recipe, "-Dhipcc=/rocm/bin/hipcc"], TensorFoldSourceBuild.CompleteArgs(recipe, BackendType.Hip, new FakeTools(), null, windows: true));
        Assert.Equal(recipe, TensorFoldSourceBuild.CompleteArgs(recipe, BackendType.Hip, new FakeTools { Path = ["hipcc"] }, null, windows: true));
        Assert.Throws<InvalidOperationException>(() => TensorFoldSourceBuild.CompleteArgs(recipe, BackendType.Hip, new FakeTools { HipccPath = null }, null, windows: true));
    }

    [Fact]
    public void Xpu_AddsOclocAndTheNgenHeaders_OnLinux()
    {
        Assert.Equal(["xpu-native", "-Dxpu-device=bmg", "-Docloc=/usr/bin/ocloc", "-Dngen-include=/tools/oneDNN/third_party/ngen"],
            TensorFoldSourceBuild.CompleteArgs(["xpu-native", "-Dxpu-device=bmg"], BackendType.Xpu, new FakeTools(), "/tools/oneDNN/third_party/ngen", windows: false));
        Assert.Throws<InvalidOperationException>(() =>
            TensorFoldSourceBuild.CompleteArgs(["xpu-native"], BackendType.Xpu, new FakeTools { Path = [] }, "/ngen", windows: false));
    }

    [Fact]
    public void Xpu_IsRefusedOnWindows_WithTheReason()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TensorFoldSourceBuild.CompleteArgs(["xpu-native"], BackendType.Xpu, new FakeTools(), "/ngen", windows: true));
        Assert.Contains("Linux", ex.Message);
    }

    [Fact]
    public void Pins_AreReadFromTheCheckout()
    {
        Assert.Equal(TensorFoldSourceBuild.DefaultZigVersion, TensorFoldSourceBuild.ZigVersionOf(_root));
        Assert.Equal(TensorFoldSourceBuild.DefaultXgrammarTag, TensorFoldSourceBuild.XgrammarTagOf(_root));

        File.WriteAllText(Path.Combine(_root, ".zig-version"), "0.18.0\n");
        var platform = Path.Combine(_root, "zig", "src", "platform");
        Directory.CreateDirectory(platform);
        File.WriteAllText(Path.Combine(platform, "grammar_library.zig"), "pub const pinned = \"v0.2.9\";\npub const clone = \"git clone ...\";\n");

        Assert.Equal("0.18.0", TensorFoldSourceBuild.ZigVersionOf(_root));
        Assert.Equal("v0.2.9", TensorFoldSourceBuild.XgrammarTagOf(_root));
    }

    [Fact]
    public async Task Collect_HipBuild_GetsTheGrammarLibraryBesideItsBin()
    {
        var ctx = Context(BackendType.Hip);
        var exe = TensorFoldLayout.ExecutableName(OperatingSystem.IsWindows());
        Write(Path.Combine(ctx.BuildDir, "native-hip", "bin", exe));
        Write(Path.Combine(ctx.BuildDir, "native", "lib", "tfgrammar.dll"));
        Write(Path.Combine(ctx.SourceDir, "LICENSE"));
        ctx.ResolvedVersionTag = "v1.2.7-3-gabcdef0";

        await new CollectZigOutputStep().ExecuteAsync(ctx, CancellationToken.None);
        await new FinalizeBuildStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(ctx.OutputDir, "bin", exe)));
        Assert.True(File.Exists(Path.Combine(ctx.OutputDir, "lib", "tfgrammar.dll")));
        Assert.True(File.Exists(Path.Combine(ctx.OutputDir, "LICENSE")));
        Assert.Equal("v1.2.7-3-gabcdef0", TensorFoldLayout.ReadVersion(ctx.OutputDir));
        Assert.Equal(Path.Combine(_root, "out"), ctx.OutputDir); // the install stays the root, not bin/
    }

    [Fact]
    public async Task Collect_WithoutTheVariantsServer_ExplainsTheRecipeShape()
    {
        var ctx = Context(BackendType.Cuda);
        Write(Path.Combine(ctx.BuildDir, "bin", "tensorfold"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new CollectZigOutputStep().ExecuteAsync(ctx, CancellationToken.None));
        Assert.Contains("-Dgpu=sm_NN", ex.Message);
    }

    [Fact]
    public void Templates_CoverEveryBackend_WithTheDocumentedSteps()
    {
        var templates = BuiltInRecipeTemplates.All().Where(r => r.Engine == ServerEngine.TensorFold).ToList();

        Assert.Contains(templates, r => r.BackendType == BackendType.Cuda && r.CMakeArgs.SequenceEqual(["-Dgpu=sm_120"]));
        Assert.Contains(templates, r => r.BackendType == BackendType.Hip && r.CMakeArgs[0] == "native-hip");
        Assert.Contains(templates, r => r.BackendType == BackendType.Xpu && r.CMakeArgs[0] == "xpu-native");
        Assert.All(templates, r => Assert.Equal(LlamaCppBuildRecipe.DefaultRepoUrl(ServerEngine.TensorFold), r.GitRepoUrl));
        Assert.Equal(templates.Count, templates.Select(r => r.Name).Distinct().Count());
    }

    private BuildContext Context(BackendType backend) => new()
    {
        BuildId = Guid.NewGuid(),
        Repo = TensorFoldLayout.Repo,
        BackendType = backend,
        Profile = TensorFoldReleaseProfile.Instance,
        WorkRoot = Path.Combine(_root, "work"),
        OutputDir = Path.Combine(_root, "out"),
        SourceDirOverride = Path.Combine(_root, "src"),
        Sink = new BuildProgressSink(Guid.NewGuid(), new NullPublisher(), Path.Combine(_root, "build.log"), NullLogger.Instance),
    };

    private static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    private sealed class NullPublisher : IEngineBuildProgressPublisher
    {
        public Task PublishAsync(EngineBuildProgress progress) => Task.CompletedTask;
    }
}
