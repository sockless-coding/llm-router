using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using System.Text.Json;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;
using LR.Providers;
using LR.Providers.Vllm;

namespace LR.Core.Tests.Providers;

public sealed class VllmProviderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vllm-test-").FullName;
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddBackendEngines().BuildServiceProvider();

    public void Dispose()
    {
        _services.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class TestableVllmProvider(ILogger<VllmProvider> logger, IServiceScopeFactory scopeFactory) : VllmProvider(logger, scopeFactory)
    {
        public ServerLaunchSpec Launch(ModelPreset preset, int port = 8123)
        {
            Port = port;
            return BuildLaunchSpec(preset);
        }

        public string Raw(string payload) => PrepareRawRequestPayload("/v1/embeddings", payload);

        public Dictionary<string, JsonElement> Chat(string payload)
        {
            var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload)!;
            PrepareChatRequestBody(body);
            return body;
        }

        public bool ProbesHealth => ReadyWhenHealthy;
        public bool LlamaEndpoints => HasLlamaServerEndpoints;
    }

    private TestableVllmProvider Create(BackendConfigData config)
    {
        var provider = ActivatorUtilities.CreateInstance<TestableVllmProvider>(_services);
        provider.Configure(config);
        return provider;
    }

    private string CreateVenv()
    {
        var relative = OperatingSystem.IsWindows() ? @".venv\Scripts\vllm.exe" : ".venv/bin/vllm";
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private static readonly ModelPreset Qwen = new() { ModelPath = "Qwen/Qwen3-8B" };

    [Fact]
    public void Local_LaunchesTheVenv_OnLoopback_FromTheInstallFolder()
    {
        var executable = CreateVenv();
        using var provider = Create(new BackendConfigData { InstallFolderPath = _root });

        var launch = provider.Launch(Qwen);

        Assert.Equal(executable, launch.ExecutablePath);
        Assert.Equal(_root, launch.WorkingDirectory);
        Assert.Equal(["serve", "Qwen/Qwen3-8B", "--served-model-name", VllmLayout.ServedModelName, "--host", "127.0.0.1", "--port", "8123"], launch.Arguments);
    }

    [Fact]
    public void Local_MissingInstall_IsAClearError()
    {
        using var provider = Create(new BackendConfigData { InstallFolderPath = _root });

        Assert.Throws<FileNotFoundException>(() => provider.Launch(Qwen));
    }

    [Fact]
    public void NoInstallFolder_IsAClearError()
    {
        using var provider = Create(new BackendConfigData());

        Assert.Throws<InvalidOperationException>(() => provider.Launch(Qwen));
    }

    [Fact]
    public void RemoteLinuxAgent_ListensOnAllInterfaces_AndDoesntCheckThisMachinesDisk()
    {
        using var provider = Create(new BackendConfigData
        {
            InstallFolderPath = "/var/lib/lr-agent/engines/vllm",
            AgentId = Guid.NewGuid(),
            RemoteHost = "10.0.0.5",
            RemoteIsWindows = false,
        });

        var launch = provider.Launch(Qwen);

        Assert.Equal("/var/lib/lr-agent/engines/vllm/.venv/bin/vllm", launch.ExecutablePath);
        Assert.Equal("/var/lib/lr-agent/engines/vllm", launch.WorkingDirectory);
        Assert.Contains("0.0.0.0", launch.Arguments);
        Assert.Equal("http://10.0.0.5:8123", provider.BaseUrl);
    }

    [Fact]
    public void RemoteWindowsAgent_GoesThroughWsl_ConvertingAModelFolder()
    {
        using var provider = Create(new BackendConfigData
        {
            InstallFolderPath = @"\\wsl.localhost\Ubuntu\home\me\.llm-router\engines\vllm",
            AgentId = Guid.NewGuid(),
            RemoteHost = "10.0.0.6",
            RemoteIsWindows = true,
        });

        var launch = provider.Launch(new ModelPreset
        {
            ModelPath = @"D:\models\qwen",
            EngineSettings = new() { [VllmPresetSettings.Environment] = "VLLM_X=1" },
        });

        Assert.Equal("wsl.exe", launch.ExecutablePath);
        Assert.Null(launch.WorkingDirectory);
        Assert.Equal(
        [
            "-d", "Ubuntu", "--cd", "/home/me/.llm-router/engines/vllm",
            "--exec", "/home/me/.llm-router/engines/vllm/.venv/bin/vllm",
            "serve", "/mnt/d/models/qwen", "--served-model-name", VllmLayout.ServedModelName,
            "--host", "0.0.0.0", "--port", "8123",
        ], launch.Arguments);
        // WSL passes on only the Windows variables WSLENV names.
        Assert.Equal("1", launch.Environment!["VLLM_X"]);
        Assert.Equal("VLLM_X", launch.Environment["WSLENV"]);
    }

    private string CreateDockerInstall()
    {
        var folder = Path.Combine(_root, VllmDocker.FolderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Dockerfile"), "");
        return folder;
    }

    private static BackendConfigData RemoteWindowsDocker() => new()
    {
        InstallFolderPath = @"D:\lr-agent\engines\" + VllmDocker.FolderName,
        AgentId = Guid.NewGuid(),
        RemoteHost = "10.0.0.6",
        RemoteIsWindows = true,
    };

    [Fact]
    public void RemoteDocker_PublishesOnAllInterfaces_ServesInsideOnAllInterfaces_AndCanBeStopped()
    {
        using var provider = Create(RemoteWindowsDocker());

        var launch = provider.Launch(Qwen);

        Assert.Equal(VllmDocker.Executable, launch.ExecutablePath);
        Assert.Contains("0.0.0.0:8123:8123", launch.Arguments);
        Assert.Equal(["serve", "Qwen/Qwen3-8B", "--served-model-name", VllmLayout.ServedModelName, "--host", "0.0.0.0", "--port", "8123"], launch.Arguments.TakeLast(8));
        Assert.Equal(VllmDocker.Executable, launch.Stop!.ExecutablePath);
        Assert.Equal(VllmDocker.StopArgs(8123), launch.Stop.Arguments);
    }

    [Fact]
    public void RemoteDocker_MountsAModelFolder_AndServesItFromInsideTheContainer()
    {
        using var provider = Create(RemoteWindowsDocker());

        var launch = provider.Launch(new ModelPreset { ModelPath = @"D:\models\qwen" });

        Assert.Contains(@"D:\models\qwen:/model:ro", launch.Arguments);
        Assert.Equal(VllmDocker.ContainerModelPath, launch.Arguments[launch.Arguments.IndexOf("serve") + 1]);
    }

    [Fact]
    public void Docker_SecretsReachTheProcessEnvironment_NotItsArguments()
    {
        using var provider = Create(RemoteWindowsDocker());

        var launch = provider.Launch(new ModelPreset
        {
            ModelPath = "m/n",
            EngineSettings = new() { [VllmPresetSettings.Environment] = "MY_SECRET=hunter2" },
        });

        Assert.Equal("hunter2", launch.Environment!["MY_SECRET"]);
        Assert.Contains("MY_SECRET", launch.Arguments);
        Assert.DoesNotContain(launch.Arguments, a => a.Contains("hunter2"));
        Assert.False(launch.Environment.ContainsKey("WSLENV"));
    }

    [Fact]
    public void LocalDocker_PublishesOnLoopback_WhenTheInstallIsThere()
    {
        // Windows only in real use; the provider asks the agent's OS, so this runs anywhere when remote is off.
        if (!OperatingSystem.IsWindows()) return;
        using var provider = Create(new BackendConfigData { InstallFolderPath = CreateDockerInstall() });

        Assert.Contains("127.0.0.1:8123:8123", provider.Launch(Qwen).Arguments);
    }

    [Fact]
    public void LocalDocker_MissingBuildFilesOrModelFolder_IsAClearError()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var missingInstall = Create(new BackendConfigData { InstallFolderPath = Path.Combine(_root, VllmDocker.FolderName) });
        Assert.Throws<FileNotFoundException>(() => missingInstall.Launch(Qwen));

        using var missingModel = Create(new BackendConfigData { InstallFolderPath = CreateDockerInstall() });
        Assert.Throws<DirectoryNotFoundException>(() => missingModel.Launch(new ModelPreset { ModelPath = Path.Combine(_root, "no-such-model") }));
    }

    [Fact]
    public void Venv_HasNoStopCommand()
    {
        CreateVenv();
        using var provider = Create(new BackendConfigData { InstallFolderPath = _root });

        Assert.Null(provider.Launch(Qwen).Stop);
    }

    [Fact]
    public void Descriptor_RecognisesADockerInstall()
    {
        var descriptor = _services.GetRequiredService<IEngineCatalog>().Get(ServerEngine.Vllm)!;

        Assert.Null(descriptor.ValidateInstallFolder(CreateDockerInstall()));
    }

    [Fact]
    public void Environment_StaysOutOfTheArguments()
    {
        CreateVenv();
        using var provider = Create(new BackendConfigData { InstallFolderPath = _root });

        var launch = provider.Launch(new ModelPreset
        {
            ModelPath = "m/n",
            EngineSettings = new() { [VllmPresetSettings.Environment] = "MY_SECRET=hunter2" },
        });

        Assert.Equal("hunter2", launch.Environment!["MY_SECRET"]);
        Assert.DoesNotContain(launch.Arguments, a => a.Contains("hunter2"));
    }

    [Fact]
    public void Requests_AreRewrittenToTheServedName_LeavingTheRestAlone()
    {
        using var provider = Create(new BackendConfigData());

        var chat = provider.Chat("""{"model":"my-preset-alias","messages":[],"stream":true}""");
        Assert.Equal(VllmLayout.ServedModelName, chat["model"].GetString());
        Assert.True(chat["stream"].GetBoolean());

        using var raw = JsonDocument.Parse(provider.Raw("""{"model":"claude-*","input":"hi"}"""));
        Assert.Equal(VllmLayout.ServedModelName, raw.RootElement.GetProperty("model").GetString());
        Assert.Equal("hi", raw.RootElement.GetProperty("input").GetString());

        Assert.Equal("not json", provider.Raw("not json"));
    }

    [Fact]
    public void Concurrency_ComesFromThePreset_WithABackstopDefault()
    {
        CreateVenv();
        using var provider = Create(new BackendConfigData { InstallFolderPath = _root });

        Assert.Equal(VllmProvider.DefaultMaxConcurrentRequests, provider.MaxConcurrentRequests);

        provider.Launch(new ModelPreset { ModelPath = "m/n", EngineSettings = new() { [VllmPresetSettings.MaxConcurrentRequests] = "8" } });
        Assert.Equal(8, provider.MaxConcurrentRequests);
    }

    [Fact]
    public void Readiness_ComesFromHealth_AndLlamaEndpointsAreSkipped()
    {
        using var provider = Create(new BackendConfigData());

        Assert.True(provider.ProbesHealth);
        Assert.False(provider.LlamaEndpoints);
    }

    [Fact]
    public void Factory_CreatesTheProvider_AndTheDescriptorIsRegistered()
    {
        var factory = _services.GetRequiredService<IBackendProviderFactory>();
        var catalog = _services.GetRequiredService<IEngineCatalog>();

        Assert.IsType<VllmProvider>(factory.Create(ServerEngine.Vllm));
        var descriptor = catalog.Get(ServerEngine.Vllm)!;
        Assert.Equal("section-vllm", descriptor.PresetFormSection);
        Assert.True(descriptor.SupportsRemoteAgents);
        Assert.Equal(EngineModality.Text, descriptor.Modality);
    }

    [Fact]
    public void Descriptor_ValidatesTheFolder()
    {
        var descriptor = _services.GetRequiredService<IEngineCatalog>().Get(ServerEngine.Vllm)!;

        Assert.Contains("does not exist", descriptor.ValidateInstallFolder(Path.Combine(_root, "nope")));
        Assert.Contains("vLLM install", descriptor.ValidateInstallFolder(_root));

        CreateVenv();
        Assert.Null(descriptor.ValidateInstallFolder(_root));
    }
}
