using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using LR.Core.Models;
using LR.Core.Services.EngineBuilds;
using LR.Providers;
using LR.Providers.TensorFold;

namespace LR.Core.Tests.Providers;

public sealed class TensorFoldDockerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tf-docker-").FullName;
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddBackendEngines().BuildServiceProvider();

    public void Dispose()
    {
        _services.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class TestableProvider(ILogger<TensorFoldProvider> logger, IServiceScopeFactory scopeFactory) : TensorFoldProvider(logger, scopeFactory)
    {
        public ServerLaunchSpec Launch(ModelPreset preset, int port = 8123)
        {
            Port = port;
            return BuildLaunchSpec(preset);
        }
    }

    private string DockerInstall()
    {
        var folder = Path.Combine(_root, TensorFoldDocker.FolderName("v1.2.7"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Dockerfile"), TensorFoldDocker.Dockerfile);
        return folder;
    }

    private TestableProvider Create(string folder)
    {
        var provider = ActivatorUtilities.CreateInstance<TestableProvider>(_services);
        provider.Configure(new BackendConfigData { InstallFolderPath = folder });
        return provider;
    }

    [Fact]
    public void Folder_IsRecognisedByName_AndValidatedByItsDockerfile()
    {
        var folder = DockerInstall();

        Assert.True(TensorFoldDocker.IsDockerFolder(folder + Path.DirectorySeparatorChar));
        Assert.True(TensorFoldDocker.IsInstall(folder));
        Assert.Null(new TensorFoldEngineDescriptor().ValidateInstallFolder(folder));
        Assert.False(TensorFoldDocker.IsDockerFolder(Path.Combine(_root, "tensorfold-1.2.7-linux-x86_64")));
    }

    [Fact]
    public void Dockerfile_PinsTheRuntime_AndBakesInTheDriverKey()
    {
        var dockerfile = TensorFoldDocker.Dockerfile;

        Assert.All(TensorFoldDocker.IntelRuntime, r => Assert.Contains(r.Url, dockerfile));
        Assert.Contains("sha256sum -c intel-runtime.sha256", dockerfile);
        Assert.Contains("AllowUnrestrictedSize=1", dockerfile);
        Assert.Contains("NEOReadDebugKeys=1", dockerfile);
        Assert.Contains("zig build xpu-native", dockerfile);
        Assert.Contains($"ENTRYPOINT [\"{TensorFoldDocker.ContainerExecutable}\"]", dockerfile);
        Assert.DoesNotContain("\r", dockerfile);
    }

    [Theory]
    [InlineData("v1.2.7", true)]
    [InlineData("main", true)]
    [InlineData("feature/xpu-fix", true)]
    [InlineData("v1.2.7; rm -rf /", false)]
    [InlineData("-Dflag", false)]
    public void Refs_AreChecked_BeforeReachingTheBuild(string gitRef, bool valid)
    {
        Assert.Equal(valid, TensorFoldDocker.IsValidRef(gitRef));
    }

    [Fact]
    public void LocalModelFolder_IsMountedReadOnly_AndServedDirectly()
    {
        if (!OperatingSystem.IsWindows()) return; // the image runs in Docker Desktop on Windows only
        var model = Directory.CreateDirectory(Path.Combine(_root, "Qwen3.8-27B-GPTQ")).FullName;
        var provider = Create(DockerInstall());

        var launch = provider.Launch(new ModelPreset { Name = "q", ModelPath = model });

        Assert.Equal("docker.exe", launch.ExecutablePath);
        var args = launch.Arguments;
        Assert.Contains("/dev/dxg", args);
        Assert.Contains($"{model}:/model:ro", args);
        Assert.Contains("127.0.0.1:8123:8123", args);
        Assert.DoesNotContain("--entrypoint", args);
        var image = args.IndexOf(TensorFoldDocker.ImageTag);
        Assert.Equal(["serve", "/model", "--name", TensorFoldPresetSettings.ServedModelName, "--host", "0.0.0.0", "--port", "8123"], args.Skip(image + 1));
        Assert.Equal(TensorFoldDocker.StopArgs(8123), launch.Stop!.Arguments);
    }

    [Fact]
    public void RepoId_IsPulledIntoTheSharedCacheVolume_First()
    {
        if (!OperatingSystem.IsWindows()) return;
        var provider = Create(DockerInstall());

        var launch = provider.Launch(new ModelPreset { Name = "q", ModelPath = "TensorFold/Qwen3.8-27B-MLX-4bit@abc" });

        var args = launch.Arguments;
        Assert.Contains($"{DockerDesktop.HuggingFaceVolume}:/hf-cache", args);
        Assert.Equal("/bin/sh", args[args.IndexOf("--entrypoint") + 1]);
        var image = args.IndexOf(TensorFoldDocker.ImageTag);
        Assert.Equal(
            ["-c", TensorFoldPresetSettings.PullThenServeScript, "sh", TensorFoldDocker.ContainerExecutable,
             "TensorFold/Qwen3.8-27B-MLX-4bit@abc", "models--TensorFold--Qwen3.8-27B-MLX-4bit", "missing", "serve", "TensorFold/Qwen3.8-27B-MLX-4bit"],
            args.Skip(image + 1).Take(9));
    }

    [Fact]
    public void LibraryDrafter_IsMountedReadOnly_AndPassedByItsContainerPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        var model = Directory.CreateDirectory(Path.Combine(_root, "Qwen3.8-27B-GPTQ")).FullName;
        var drafter = Directory.CreateDirectory(Path.Combine(_root, "Qwen3.8-27B-DFlash2")).FullName;
        var provider = Create(DockerInstall());

        var launch = provider.Launch(new ModelPreset
        {
            Name = "q",
            ModelPath = model,
            EngineSettings = new() { [TensorFoldPresetSettings.Drafter] = drafter },
        });

        var args = launch.Arguments;
        Assert.Contains($"{drafter}:{TensorFoldDocker.ContainerDrafterPath}:ro", args);
        Assert.Equal(TensorFoldDocker.ContainerDrafterPath, args[args.LastIndexOf("--drafter") + 1]);
    }

    [Fact]
    public void RepoIdDrafter_IsLeftForTheCacheVolume()
    {
        if (!OperatingSystem.IsWindows()) return;
        var provider = Create(DockerInstall());

        var launch = provider.Launch(new ModelPreset
        {
            Name = "q",
            ModelPath = "owner/model",
            EngineSettings = new() { [TensorFoldPresetSettings.Drafter] = "z-lab/Qwen3.8-27B-DFlash2" },
        });

        Assert.DoesNotContain(launch.Arguments, a => a.EndsWith($":{TensorFoldDocker.ContainerDrafterPath}:ro", StringComparison.Ordinal));
        Assert.Equal("z-lab/Qwen3.8-27B-DFlash2", launch.Arguments[launch.Arguments.LastIndexOf("--drafter") + 1]);
    }

    [Fact]
    public void MissingDrafterFolder_FailsTheStart()
    {
        if (!OperatingSystem.IsWindows()) return;
        var model = Directory.CreateDirectory(Path.Combine(_root, "m")).FullName;
        var provider = Create(DockerInstall());

        Assert.Throws<DirectoryNotFoundException>(() => provider.Launch(new ModelPreset
        {
            Name = "q",
            ModelPath = model,
            EngineSettings = new() { [TensorFoldPresetSettings.Drafter] = Path.Combine(_root, "no-such-drafter") },
        }));
    }

    [Fact]
    public void NetworkModelFolder_IsRefused()
    {
        if (!OperatingSystem.IsWindows()) return;
        var provider = Create(DockerInstall());

        Assert.Throws<InvalidOperationException>(() => provider.Launch(new ModelPreset { Name = "q", ModelPath = @"\\nas\models\qwen" }));
    }
}
