using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Core.Services.EngineBuilds;
using LR.Providers;
using LR.Providers.TensorFold;

namespace LR.Core.Tests.Providers;

public sealed class TensorFoldProviderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tensorfold-test-").FullName;
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddBackendEngines().BuildServiceProvider();

    public void Dispose()
    {
        _services.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private sealed class TestableTensorFoldProvider(ILogger<TensorFoldProvider> logger, IServiceScopeFactory scopeFactory)
        : TensorFoldProvider(logger, scopeFactory)
    {
        public ServerLaunchSpec Launch(ModelPreset preset, int port = 8123)
        {
            Port = port;
            return BuildLaunchSpec(preset);
        }

        public Dictionary<string, JsonElement> Chat(string payload)
        {
            var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload)!;
            PrepareChatRequestBody(body);
            return body;
        }

        public string Raw(string payload) => PrepareRawRequestPayload("/v1/completions", payload);

        public bool ProbesHealth => ReadyWhenHealthy;
        public bool LlamaEndpoints => HasLlamaServerEndpoints;
    }

    private TestableTensorFoldProvider Create(BackendConfigData config)
    {
        var provider = ActivatorUtilities.CreateInstance<TestableTensorFoldProvider>(_services);
        provider.Configure(config);
        return provider;
    }

    private static ModelPreset Preset(string model, params (string Key, string Value)[] settings) => new()
    {
        Name = "tf",
        ModelPath = model,
        EngineSettings = settings.ToDictionary(s => s.Key, s => s.Value),
    };

    private static BackendConfigData RemoteAgent(bool windows) => new()
    {
        InstallFolderPath = windows ? @"C:\tf\zig-out\native" : "/opt/tensorfold",
        AgentId = Guid.NewGuid(),
        RemoteHost = "10.0.0.7",
        RemoteIsWindows = windows,
    };

    // --- release profile ---

    private static readonly List<GitHubReleaseAsset> Assets = new[]
    {
        "tensorfold-1.2.7-linux-aarch64.tar.gz",
        "tensorfold-1.2.7-linux-aarch64.tar.gz.sha256",
        "tensorfold-1.2.7-linux-aarch64.tar.gz.sha256.sig",
        "tensorfold-1.2.7-linux-x86_64.tar.gz",
        "tensorfold-1.2.7-linux-x86_64.tar.gz.sha256",
        "tensorfold-1.2.7-macos-arm64.tar.gz",
        "tensorfold-engine-1.2.6-darwin-arm64-metal.tar.gz",
    }.Select(n => new GitHubReleaseAsset { Name = n }).ToList();

    [Theory]
    [InlineData(BackendType.Cuda, "ubuntu", "x64", "tensorfold-1.2.7-linux-x86_64.tar.gz")]
    [InlineData(BackendType.Cuda, "ubuntu", "arm64", "tensorfold-1.2.7-linux-aarch64.tar.gz")]
    [InlineData(BackendType.Metal, "macos", "arm64", "tensorfold-1.2.7-macos-arm64.tar.gz")]
    public void ReleaseProfile_PicksTheEngineArchive(BackendType backend, string os, string arch, string expected)
    {
        var picked = TensorFoldReleaseProfile.Instance.Resolve(Assets, backend, os, arch);

        Assert.Equal([expected], picked.Select(a => a.Name));
    }

    [Fact]
    public void ReleaseProfile_HasNoWindowsOrCpuArchive()
    {
        Assert.False(TensorFoldReleaseProfile.Instance.IsAvailable(Assets, BackendType.Cuda, "win", "x64"));
        Assert.False(TensorFoldReleaseProfile.Instance.IsAvailable(Assets, BackendType.Cpu, "ubuntu", "x64"));
        Assert.False(TensorFoldReleaseProfile.Instance.IsAvailable(Assets, BackendType.Metal, "ubuntu", "arm64"));
    }

    [Theory]
    [InlineData("v1.2.7", true)]
    [InlineData("v10.0.1", true)]
    [InlineData("app-v1.0.0", false)]
    [InlineData("v1.0.0-beta.9", false)]
    [InlineData("1.2.7", false)]
    public void ReleaseProfile_InstallableTags(string tag, bool installable)
    {
        Assert.Equal(installable, TensorFoldReleaseProfile.Instance.IsInstallableRelease(tag));
    }

    [Fact]
    public async Task ReleaseProfile_Latest_SkipsTheDesktopAppsReleases()
    {
        var github = new StubGitHub
        {
            Releases =
            [
                new GitHubRelease { TagName = "app-v1.1.0" },
                new GitHubRelease { TagName = "v1.3.0-rc1", Prerelease = true },
                new GitHubRelease { TagName = "v1.2.8" },
                new GitHubRelease { TagName = "v1.2.7" },
            ],
        };

        var latest = await TensorFoldReleaseProfile.Instance.GetLatestReleaseAsync(github);

        Assert.Equal("v1.2.8", latest?.TagName);
        Assert.Equal("v1.2.8", await new TensorFoldInstallHandler().GetLatestRefAsync(new EngineBuild(), github));
    }

    [Fact]
    public async Task Finalize_KeepsTheArchiveRoot_SoLibStaysBesideBin()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(_root, "lib"));
        File.WriteAllText(Path.Combine(bin, TensorFoldReleaseProfile.Instance.ServerExecutableName), "");

        var ctx = new BuildContext
        {
            BuildId = Guid.NewGuid(),
            Repo = TensorFoldLayout.Repo,
            BackendType = BackendType.Cuda,
            Profile = TensorFoldReleaseProfile.Instance,
            WorkRoot = _root,
            OutputDir = _root,
            Sink = new BuildProgressSink(Guid.NewGuid(), new NullPublisher(), Path.Combine(_root, "build.log"), NullLogger.Instance),
        };

        await new FinalizeBuildStep().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(_root, ctx.OutputDir);
        Assert.True(new TensorFoldInstallHandler().IsInstallPresent(_root));
    }

    // --- preset settings ---

    [Fact]
    public void ServeArgs_RepoId_DropsTheRevision_AndMapsTheSettings()
    {
        var preset = Preset("TensorFold/Qwen3.8-27B-MLX-4bit@abc123",
            (TensorFoldPresetSettings.Context, "32768"),
            (TensorFoldPresetSettings.Parallel, "4"),
            (TensorFoldPresetSettings.MaxTokens, "2048"),
            (TensorFoldPresetSettings.Thinking, "off"),
            (TensorFoldPresetSettings.ReasoningEffort, "high"),
            (TensorFoldPresetSettings.Drafter, "z-lab/Qwen3.8-27B-DFlash2"),
            (TensorFoldPresetSettings.Vision, "true"),
            (TensorFoldPresetSettings.ExtraArgs, "--temperature\n0.6\n# a comment\n"));

        Assert.Equal(
            [
                "serve", "TensorFold/Qwen3.8-27B-MLX-4bit", "--name", TensorFoldPresetSettings.ServedModelName,
                "--context", "32768", "--parallel", "4", "--max-tokens", "2048", "--no-thinking",
                "--reasoning-effort", "high", "--drafter", "z-lab/Qwen3.8-27B-DFlash2", "--vision",
                "--temperature", "0.6",
            ],
            TensorFoldPresetSettings.ServeArgs(preset));
        Assert.Equal(4, TensorFoldPresetSettings.MaxConcurrent(preset));
        Assert.Equal("TensorFold/Qwen3.8-27B-MLX-4bit@abc123", TensorFoldPresetSettings.PullRef(preset));
    }

    [Fact]
    public void ServeArgs_Defaults_LeaveEverythingToTensorFold()
    {
        var preset = Preset("mlx-community/Qwen3.5-2B-MLX-4bit", (TensorFoldPresetSettings.Parallel, "auto"), (TensorFoldPresetSettings.Vision, "false"));

        Assert.Equal(["serve", "mlx-community/Qwen3.5-2B-MLX-4bit", "--name", TensorFoldPresetSettings.ServedModelName],
            TensorFoldPresetSettings.ServeArgs(preset));
        Assert.Equal(TensorFoldPresetSettings.DefaultMaxConcurrent, TensorFoldPresetSettings.MaxConcurrent(preset));
    }

    [Theory]
    [InlineData("--port\n9000")]
    [InlineData("--host=0.0.0.0")]
    [InlineData("--api-key\nsecret")]
    public void ServeArgs_RefusesFlagsTheRouterOwns(string extra)
    {
        var preset = Preset("a/b", (TensorFoldPresetSettings.ExtraArgs, extra));

        Assert.Throws<InvalidOperationException>(() => TensorFoldPresetSettings.ServeArgs(preset));
    }

    [Theory]
    [InlineData(TensorFoldPresetSettings.Context, "lots")]
    [InlineData(TensorFoldPresetSettings.Parallel, "0")]
    [InlineData(TensorFoldPresetSettings.Thinking, "maybe")]
    [InlineData(TensorFoldPresetSettings.Pull, "sometimes")]
    public void Settings_RejectValuesTensorFoldWouldNot(string key, string value)
    {
        var preset = Preset("a/b", (key, value));

        Assert.Throws<InvalidOperationException>(() =>
        {
            TensorFoldPresetSettings.ServeArgs(preset);
            TensorFoldPresetSettings.PullRef(preset);
        });
    }

    [Theory]
    [InlineData("/models/qwen", null)]
    [InlineData(@"C:\models\qwen", null)]
    [InlineData("owner/name", "owner/name")]
    public void PullRef_OnlyForRepoIds(string model, string? expected)
    {
        Assert.Equal(expected, TensorFoldPresetSettings.PullRef(Preset(model)));
        Assert.Null(TensorFoldPresetSettings.PullRef(Preset(model, (TensorFoldPresetSettings.Pull, "never"))));
    }

    [Fact]
    public void Environment_RefusesProtectedVariables()
    {
        Assert.Equal("1", TensorFoldPresetSettings.EnvironmentOf(Preset("a/b", (TensorFoldPresetSettings.Environment, "TF_DEBUG=1")))["TF_DEBUG"]);
        Assert.Throws<InvalidOperationException>(() =>
            TensorFoldPresetSettings.EnvironmentOf(Preset("a/b", (TensorFoldPresetSettings.Environment, "LD_PRELOAD=/tmp/x.so"))));
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("0.5", "0.5")]
    [InlineData("0", "0")]
    public void ServeArgs_PassesThePromptCacheSize(string value, string expected)
    {
        var args = TensorFoldPresetSettings.ServeArgs(Preset("a/b", (TensorFoldPresetSettings.PromptCacheGib, value)));

        Assert.Equal(["--prompt-cache-gib", expected], args.Skip(4));
    }

    [Theory]
    [InlineData(TensorFoldPresetSettings.PromptCacheGib, "-1")]
    [InlineData(TensorFoldPresetSettings.PromptCacheGib, "lots")]
    [InlineData(TensorFoldPresetSettings.WddmChunkMb, "0")]
    [InlineData(TensorFoldPresetSettings.WddmChunkMb, "4k")]
    public void NumericSettings_RejectBadValues(string key, string value)
    {
        var preset = Preset("a/b", (key, value));

        Assert.Throws<InvalidOperationException>(() =>
        {
            TensorFoldPresetSettings.ServeArgs(preset);
            TensorFoldPresetSettings.EnvironmentOf(preset);
        });
    }

    [Fact]
    public void Environment_WddmChunkSize_SetsIntelsDebugKeys_AndTypedLinesWin()
    {
        var env = TensorFoldPresetSettings.EnvironmentOf(Preset("a/b", (TensorFoldPresetSettings.WddmChunkMb, "4096")));
        Assert.Equal("4096", env[TensorFoldPresetSettings.WddmChunkVariable]);
        Assert.Equal("1", env["NEOReadDebugKeys"]);

        var overridden = TensorFoldPresetSettings.EnvironmentOf(Preset("a/b",
            (TensorFoldPresetSettings.WddmChunkMb, "4096"),
            (TensorFoldPresetSettings.Environment, "ForceWddmHugeChunkSizeMB=2048")));
        Assert.Equal("2048", overridden[TensorFoldPresetSettings.WddmChunkVariable]);

        Assert.DoesNotContain(TensorFoldPresetSettings.WddmChunkVariable, TensorFoldPresetSettings.EnvironmentOf(Preset("a/b")).Keys);
    }

    [Fact]
    public void MemoryEstimate_IsThePresetsOwnFigure()
    {
        var preset = Preset("a/b", (TensorFoldPresetSettings.VramGb, "20.5"));

        Assert.Equal((long)(20.5 * 1024 * 1024 * 1024), PresetMemoryEstimator.EstimateBytes(preset, null, _ => null, ServerEngine.TensorFold));
    }

    // --- provider ---

    [Fact]
    public void RemoteLinuxAgent_PullsThroughTheShell_WithEveryValueAsAnArgument()
    {
        var provider = Create(RemoteAgent(windows: false));

        var launch = provider.Launch(Preset("TensorFold/Model-4bit@rev1"));

        Assert.Equal("/bin/sh", launch.ExecutablePath);
        Assert.Equal(
            [
                "-c", TensorFoldPresetSettings.PullThenServeScript, "sh",
                "/opt/tensorfold/bin/tensorfold-native", "TensorFold/Model-4bit@rev1", "models--TensorFold--Model-4bit", "missing",
                "serve", "TensorFold/Model-4bit", "--name", TensorFoldPresetSettings.ServedModelName,
                "--host", "0.0.0.0", "--port", "8123",
            ],
            launch.Arguments);
        Assert.Null(launch.WorkingDirectory);
        Assert.True(provider.ProbesHealth);
        Assert.False(provider.LlamaEndpoints);
    }

    [Fact]
    public void RemoteLinuxAgent_CheckpointFolder_ServesDirectly()
    {
        var provider = Create(RemoteAgent(windows: false));

        var launch = provider.Launch(Preset("/data/checkpoints/qwen"));

        Assert.Equal("/opt/tensorfold/bin/tensorfold-native", launch.ExecutablePath);
        Assert.Equal(["serve", "/data/checkpoints/qwen", "--name", TensorFoldPresetSettings.ServedModelName, "--host", "0.0.0.0", "--port", "8123"],
            launch.Arguments);
    }

    [Fact]
    public void RemoteWindowsAgent_ServesDirectly_WithoutPulling()
    {
        var provider = Create(RemoteAgent(windows: true));

        var launch = provider.Launch(Preset("owner/name"));

        Assert.Equal(@"C:\tf\zig-out\native\bin\tensorfold-native.exe", launch.ExecutablePath);
        Assert.Equal("serve", launch.Arguments[0]);
    }

    [Fact]
    public void LocalFolder_WithoutTheExecutable_Throws()
    {
        var provider = Create(new BackendConfigData { InstallFolderPath = _root });

        Assert.Throws<FileNotFoundException>(() => provider.Launch(Preset("owner/name")));
    }

    [Fact]
    public void LocalFolder_FindsAnExecutableBuiltFromSource()
    {
        var exe = Path.Combine(_root, TensorFoldLayout.ExecutableName(OperatingSystem.IsWindows()));
        File.WriteAllText(exe, "");
        var provider = Create(new BackendConfigData { InstallFolderPath = _root });

        var launch = provider.Launch(Preset("owner/name", (TensorFoldPresetSettings.Pull, "never")));

        Assert.Equal(exe, launch.ExecutablePath);
        Assert.Null(new TensorFoldEngineDescriptor().ValidateInstallFolder(_root));
    }

    [Fact]
    public void Requests_AreRewrittenToTheServedName_AndAskForUsageWhenStreaming()
    {
        var provider = Create(RemoteAgent(windows: false));

        var streamed = provider.Chat("""{"model":"my-preset","stream":true,"messages":[]}""");
        Assert.Equal(TensorFoldPresetSettings.ServedModelName, streamed["model"].GetString());
        Assert.True(streamed["stream_options"].GetProperty("include_usage").GetBoolean());

        var whole = provider.Chat("""{"model":"my-preset","stream_options":{"include_usage":true},"messages":[]}""");
        Assert.False(whole.ContainsKey("stream_options"));

        Assert.Contains(TensorFoldPresetSettings.ServedModelName, provider.Raw("""{"model":"alias","prompt":"hi"}"""));
    }

    [Fact]
    public void PullScript_PullsOnlyWhenTheCacheFolderIsMissing()
    {
        // Runs the real script with a stand-in executable that logs its arguments.
        if (OperatingSystem.IsWindows())
            return;

        var hub = Path.Combine(_root, "hub");
        var log = Path.Combine(_root, "calls.log");
        var exe = Path.Combine(_root, "tensorfold-native");
        File.WriteAllText(exe, $"#!/bin/sh\necho \"$@\" >> '{log}'\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string Run()
        {
            var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in new[] { "-c", TensorFoldPresetSettings.PullThenServeScript, "sh", exe, "o/n@r", "models--o--n", "missing", "serve", "o/n", "--name", "x y" })
                psi.ArgumentList.Add(a);
            psi.Environment["HF_HUB_CACHE"] = hub;
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
            var calls = File.ReadAllText(log);
            File.Delete(log);
            return calls;
        }

        Assert.Equal("pull o/n@r\nserve o/n --name x y\n", Run());

        Directory.CreateDirectory(Path.Combine(hub, "models--o--n", "snapshots"));
        Assert.Equal("serve o/n --name x y\n", Run());
    }

    private sealed class NullPublisher : IEngineBuildProgressPublisher
    {
        public Task PublishAsync(EngineBuildProgress progress) => Task.CompletedTask;
    }

    private sealed class StubGitHub : IGitHubClient
    {
        public List<GitHubRelease> Releases { get; init; } = [];

        public Task<GitHubRelease?> GetLatestReleaseAsync(string repo, CancellationToken ct = default) => Task.FromResult(Releases.FirstOrDefault());
        public Task<GitHubRelease?> GetReleaseByTagAsync(string repo, string tag, CancellationToken ct = default) => Task.FromResult(Releases.FirstOrDefault(r => r.TagName == tag));
        public Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string repo, int limit = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GitHubRelease>>(Releases);
        public Task<GitHubCompareResult?> CompareAsync(string repo, string baseRef, string headRef, CancellationToken ct = default) => Task.FromResult<GitHubCompareResult?>(null);
        public Task<string?> GetRawFileAsync(string repo, string reference, string path, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DownloadAssetAsync(string downloadUrl, string destinationPath, IProgress<EngineBuildProgress>? progress, CancellationToken ct = default) => Task.CompletedTask;
    }
}
