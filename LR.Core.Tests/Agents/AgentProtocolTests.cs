using LR.Core.Agents;
using LR.Core.Models;
using LR.Core.Wrapper;

namespace LR.Core.Tests.Agents;

public class AgentPathsTests
{
    [Theory]
    [InlineData(false, new[] { "/var/lib/lr-agent", "engines", "b1234-cuda" }, "/var/lib/lr-agent/engines/b1234-cuda")]
    [InlineData(false, new[] { "/var/lib/lr-agent/", "/models/", "repo_x", "sub/file.gguf" }, "/var/lib/lr-agent/models/repo_x/sub/file.gguf")]
    [InlineData(true, new[] { @"C:\ProgramData\LR Agent", "engines", "b1" }, @"C:\ProgramData\LR Agent\engines\b1")]
    [InlineData(true, new[] { @"D:\models\", "vae/diffusion.safetensors" }, @"D:\models\vae\diffusion.safetensors")]
    [InlineData(true, new[] { "C:", "models" }, @"C:\models")]
    [InlineData(false, new[] { "/", "opt" }, "/opt")]
    public void CombinesWithTheTargetSeparator(bool windows, string[] parts, string expected) =>
        Assert.Equal(expected, AgentPaths.Combine(windows, parts));

    [Fact]
    public void ConvertsSeparatorsOfAWindowsRouterPathForALinuxAgent() =>
        Assert.Equal("/srv/a/b", AgentPaths.Combine(false, "/srv", @"a\b"));

    [Theory]
    [InlineData(true, "llama-server", "llama-server.exe")]
    [InlineData(false, "llama-server", "llama-server")]
    [InlineData(false, "sd-server.exe", "sd-server")]
    [InlineData(true, "sd-server.exe", "sd-server.exe")]
    public void NamesExecutablesForTheTargetOs(bool windows, string name, string expected) =>
        Assert.Equal(expected, AgentPaths.Executable(windows, name));

    [Theory]
    [InlineData("/srv/models/a.gguf", "a.gguf")]
    [InlineData(@"D:\models\b.gguf", "b.gguf")]
    [InlineData("c.gguf", "c.gguf")]
    public void TakesTheFileNameWhicheverSeparatorIsUsed(string path, string expected) =>
        Assert.Equal(expected, AgentPaths.FileName(path));

    [Fact]
    public void MakesNamesSafeAsAFolder() =>
        Assert.Equal("org_repo_ x_", AgentPaths.SafeSegment("org/repo: x?"));
}

public class AgentProtocolTests
{
    [Fact]
    public void WrapperFramesRoundTripWithTheirNestedCommand()
    {
        var instanceId = Guid.NewGuid();
        var json = AgentProtocol.Serialize(new WrapperFrameMessage
        {
            InstanceId = instanceId,
            Frame = new StartServerCommand { ExecutablePath = "/opt/llama-server", Arguments = ["-m", "/m.gguf"], Port = 8081 },
        });

        var parsed = Assert.IsType<WrapperFrameMessage>(AgentProtocol.Deserialize(json));
        Assert.Equal(instanceId, parsed.InstanceId);
        var start = Assert.IsType<StartServerCommand>(parsed.Frame);
        Assert.Equal("/opt/llama-server", start.ExecutablePath);
        Assert.Equal(["-m", "/m.gguf"], start.Arguments);
        Assert.Equal(8081, start.Port);
    }

    [Fact]
    public void WrapperEventsRoundTrip()
    {
        var json = AgentProtocol.Serialize(new WrapperFrameMessage
        {
            InstanceId = Guid.NewGuid(),
            Frame = new OutputLineEvent { Stream = WrapperOutputStream.Stderr, Line = "llama_server: model loaded" },
        });
        var output = Assert.IsType<OutputLineEvent>(Assert.IsType<WrapperFrameMessage>(AgentProtocol.Deserialize(json)).Frame);
        Assert.Equal(WrapperOutputStream.Stderr, output.Stream);
        Assert.Equal("llama_server: model loaded", output.Line);
    }

    [Fact]
    public void AttachedCarriesTheHello()
    {
        var json = AgentProtocol.Serialize(new WrapperAttachedMessage
        {
            InstanceId = Guid.NewGuid(),
            Hello = new HelloEvent { WrapperPid = 42, ServerRunning = true, Port = 8080, RecentOutputBacklog = ["a", "b"] },
        });
        var attached = Assert.IsType<WrapperAttachedMessage>(AgentProtocol.Deserialize(json));
        Assert.True(attached.Hello.ServerRunning);
        Assert.Equal(42, attached.Hello.WrapperPid);
        Assert.Equal(["a", "b"], attached.Hello.RecentOutputBacklog);
    }

    [Fact]
    public void InstallJobsCarryTheirRecipe()
    {
        var json = AgentProtocol.Serialize(new InstallEngineJob
        {
            JobId = Guid.NewGuid(),
            Engine = ServerEngine.LlamaCpp,
            Backend = BackendType.Vulkan,
            Recipe = new LlamaCppBuildRecipe { Name = "vk", GitRef = "b9000", BackendType = BackendType.Vulkan },
            OutputDir = "/var/lib/lr-agent/engines/x",
            InPlace = true,
        });
        var job = Assert.IsType<InstallEngineJob>(AgentProtocol.Deserialize(json));
        Assert.Equal(BackendType.Vulkan, job.Backend);
        Assert.Equal("b9000", job.Recipe?.GitRef);
        Assert.True(job.InPlace);
    }

    [Fact]
    public void VllmInstallJobsCarryTheirVersionAndDistro()
    {
        var json = AgentProtocol.Serialize(new InstallEngineJob
        {
            JobId = Guid.NewGuid(),
            Engine = ServerEngine.Vllm,
            Backend = BackendType.Cuda,
            ReleaseTag = "v0.33.0",
            OutputDir = "/var/lib/lr-agent/engines/vllm",
            WslDistro = "Ubuntu",
        });
        var job = Assert.IsType<InstallEngineJob>(AgentProtocol.Deserialize(json));
        Assert.Equal(ServerEngine.Vllm, job.Engine);
        Assert.Equal("v0.33.0", job.ReleaseTag);
        Assert.Equal("Ubuntu", job.WslDistro);
    }

    [Fact]
    public void StartCommandsCarryTheirEnvironmentToTheAgentsWrapper()
    {
        var json = AgentProtocol.Serialize(new WrapperFrameMessage
        {
            InstanceId = Guid.NewGuid(),
            Frame = new StartServerCommand { ExecutablePath = "wsl.exe", Environment = new() { ["HF_TOKEN"] = "hf_x", ["WSLENV"] = "HF_TOKEN" } },
        });
        var frame = Assert.IsType<WrapperFrameMessage>(AgentProtocol.Deserialize(json));
        var start = Assert.IsType<StartServerCommand>(frame.Frame);
        Assert.Equal("hf_x", start.Environment!["HF_TOKEN"]);
    }

    [Fact]
    public void EnumsTravelAsNames() =>
        Assert.Contains("\"Vulkan\"", AgentProtocol.Serialize(new InstallEngineJob { Backend = BackendType.Vulkan }));

    [Theory]
    [InlineData("linux-x64", "lr-agent-linux-x64.tar.gz")]
    [InlineData("linux-arm64", "lr-agent-linux-arm64.tar.gz")]
    [InlineData("win-x64", "lr-agent-win-x64.zip")]
    public void PackagesAreZipOnWindowsAndTarballsElsewhere(string rid, string expected) =>
        Assert.Equal(expected, AgentProtocol.PackageFileName(rid));
}

public class WrapperScriptTests
{
    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("with space", "'with space'")]
    [InlineData("it's", @"'it'\''s'")]
    [InlineData("$HOME `x`", "'$HOME `x`'")]
    public void QuotesForPosixShells(string value, string expected) =>
        Assert.Equal(expected, WrapperHost.PosixQuote(value));
}
