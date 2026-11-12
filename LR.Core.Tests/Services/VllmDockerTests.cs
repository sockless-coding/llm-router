using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Tests.Services;

public class VllmDockerTests
{
    [Theory]
    [InlineData(@"D:\engines\vllm-v0.33.0-xpu-docker", true)]
    [InlineData(@"D:\engines\vllm-v0.33.0-xpu-docker\", true)]
    [InlineData("/data/engines/vllm-v1-XPU-DOCKER", true)]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\me\.llm-router\engines\vllm-v0.33.0-cuda", false)]
    [InlineData(@"D:\xpu-docker\vllm", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDockerFolder_GoesByTheFolderName(string? folder, bool expected)
    {
        Assert.Equal(expected, VllmDocker.IsDockerFolder(folder));
    }

    [Fact]
    public void InstallFolderName_MarksIntelInstallsAsDocker_AndLeavesVenvsAlone()
    {
        Assert.True(VllmDocker.IsDockerFolder(VllmLayout.InstallFolderName("v1.0", BackendType.Sycl)));
        Assert.Equal("vllm-v1.0-cuda", VllmLayout.InstallFolderName("v1.0", BackendType.Cuda));
        Assert.False(VllmDocker.IsDockerFolder(VllmLayout.InstallFolderName("v1.0", BackendType.Cuda)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(VllmDocker.PinnedVersion)]
    [InlineData("v" + VllmDocker.PinnedVersion)]
    public void ResolveTag_IsAlwaysThePinnedVersion(string? version)
    {
        Assert.Equal("v" + VllmDocker.PinnedVersion, VllmDocker.ResolveTag(version));
    }

    [Theory]
    [InlineData("0.27.2rc1.dev77")]
    [InlineData("0.33.0; rm -rf /")]
    public void ResolveTag_RefusesAnythingElse(string version)
    {
        Assert.Throws<InvalidOperationException>(() => VllmDocker.ResolveTag(version));
    }

    [Theory]
    [InlineData("Qwen/Qwen3-8B", null)]
    [InlineData(@"D:\models\qwen", @"D:\models\qwen")]
    [InlineData(@"D:\models\qwen\", @"D:\models\qwen")]
    [InlineData("D:/models/qwen", "D:/models/qwen")]
    [InlineData(@"D:\", @"D:\")]
    public void ModelFolder_TellsAFolderFromAHuggingFaceId(string model, string? expected)
    {
        Assert.Equal(expected, VllmDocker.ModelFolder(model));
    }

    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\models\qwen")]
    [InlineData(@"\\nas\models\qwen")]
    public void ModelFolder_RefusesANetworkPath(string model)
    {
        Assert.Throws<InvalidOperationException>(() => VllmDocker.ModelFolder(model));
    }

    [Fact]
    public void RunArgs_PublishTheServerAndReachTheGpu_WithoutPrivileges()
    {
        var args = VllmDocker.RunArgs(8123, "127.0.0.1", @"D:\my models\qwen", ["HF_TOKEN"], ["serve", "/model"]);

        Assert.Equal(["--host", "npipe:////./pipe/dockerDesktopLinuxEngine", "run", "--rm", "--name", "llm-router-vllm-8123"], args.Take(6));
        Assert.Equal("127.0.0.1:8123:8123", After(args, "--publish"));
        Assert.Equal("/dev/dxg", After(args, "--device"));
        Assert.Contains("/usr/lib/wsl/lib:/usr/lib/wsl/lib:ro", args);
        Assert.Contains("/usr/lib/wsl/drivers:/usr/lib/wsl/drivers:ro", args);
        Assert.Contains(@"D:\my models\qwen:/model:ro", args);
        Assert.Contains($"{VllmDocker.HuggingFaceVolume}:/hf-cache", args);
        Assert.DoesNotContain("--privileged", args);
        Assert.Equal([VllmDocker.ImageTag, "serve", "/model"], args.TakeLast(3));
    }

    [Fact]
    public void RunArgs_PassVariablesByNameOnly()
    {
        var args = VllmDocker.RunArgs(8123, "0.0.0.0", null, ["HF_TOKEN", "VLLM_X"], ["serve", "m/n"]);

        Assert.Equal("HF_TOKEN", After(args, "--env"));
        Assert.Contains("VLLM_X", args);
        Assert.DoesNotContain(args, a => a.Contains('=') && a.StartsWith("HF_TOKEN", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.EndsWith(":/model:ro", StringComparison.Ordinal));
    }

    [Fact]
    public void StopArgs_RemoveTheContainerRunArgsNamed()
    {
        Assert.Equal(["--host", "npipe:////./pipe/dockerDesktopLinuxEngine", "rm", "--force", VllmDocker.ContainerName(9000)], VllmDocker.StopArgs(9000));
    }

    [Theory]
    [InlineData("WORKDIR /opt/llm-router")] // Not /workspace/vllm, whose unpatched copy of vLLM "python -m" would import.
    [InlineData("CUDA_VISIBLE_DEVICES=-1")]
    [InlineData("TRITON_DEFAULT_BACKEND=intel")]
    [InlineData("VLLM_TARGET_DEVICE=xpu")]
    [InlineData("LD_LIBRARY_PATH=/usr/lib/wsl/lib:")]
    [InlineData("RUN python /opt/llm-router/patch_vllm.py")]
    [InlineData("FROM " + VllmDocker.BaseImage)]
    public void Dockerfile_HasWhatTheImageNeeds(string line)
    {
        Assert.Contains(line, VllmDocker.Dockerfile);
    }

    [Fact]
    public void Dockerfile_AppliesTheCookbookPatchesInOrder_AsOneValidCommand()
    {
        var dockerfile = VllmDocker.Dockerfile;
        var positions = VllmDocker.CookbookPatches.Select(p => dockerfile.IndexOf($"&& python /opt/llm-router/cookbook/{p} \\\n", StringComparison.Ordinal)).ToList();

        Assert.All(positions, p => Assert.True(p > 0));
        Assert.Equal(positions.Order(), positions);
    }

    [Fact]
    public void Qwen38Preset_TurnsOnMtpAndLowReasoning_AndIsValidForTheServer()
    {
        var preset = new ModelPreset { ModelPath = "m/n", EngineSettings = new(VllmDocker.Qwen38Preset) };

        var args = VllmPresetSettings.ServeArgs(preset);
        var environment = VllmPresetSettings.EnvironmentOf(preset);

        Assert.Equal("""{"method":"mtp","num_speculative_tokens":4}""", After(args, "--speculative-config"));
        Assert.Equal("""{"reasoning_effort":"low"}""", After(args, "--default-chat-template-kwargs"));
        Assert.Equal("1", environment["B70_MTP_BF16_DRAFT"]);
        Assert.Equal("1", environment["B70_DRAFT_LMHEAD_INT4"]);
        Assert.Equal("1", environment["B70_DRAFT_MTP_INT4"]);
    }

    [Fact]
    public void GeneratedFiles_HaveLinuxLineEndings()
    {
        Assert.DoesNotContain("\r", VllmDocker.Dockerfile);
        Assert.DoesNotContain("\r", VllmDocker.PatchScript);
        Assert.DoesNotContain("\r", VllmDocker.ProbeScript);
    }

    private static string After(List<string> args, string flag) => args[args.IndexOf(flag) + 1];
}
