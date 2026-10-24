using System.Text.Json.Nodes;

using LR.Core.Models;
using LR.Core.Services;
using LR.Core.Services.EngineBuilds;
using LR.Providers.StableDiffusionCpp;

namespace LR.Core.Tests.Providers;

public class StableDiffusionCppTests
{
    private static ModelPreset Preset(string modelPath, params (string Key, string Value)[] settings) => new()
    {
        Name = "img",
        ModelPath = modelPath,
        EngineSettings = settings.ToDictionary(s => s.Key, s => s.Value),
    };

    // --- launch arguments ---

    [Fact]
    public void ServerArgs_CheckpointAlone_UsesModelFlag()
    {
        var args = StableDiffusionPresetSettings.ServerArgs(Preset(@"C:\m\sdxl.safetensors"));

        Assert.Equal(["-m", @"C:\m\sdxl.safetensors"], args);
    }

    [Fact]
    public void ServerArgs_WithVaeAndEncoders_DetectsStandaloneDiffusionModel()
    {
        var args = StableDiffusionPresetSettings.ServerArgs(Preset("flux.gguf",
            (StableDiffusionPresetSettings.Vae, "ae.safetensors"),
            (StableDiffusionPresetSettings.ClipL, "clip_l.safetensors"),
            (StableDiffusionPresetSettings.T5xxl, "t5xxl.gguf")));

        Assert.Equal(
            ["--diffusion-model", "flux.gguf", "--vae", "ae.safetensors", "--clip_l", "clip_l.safetensors", "--t5xxl", "t5xxl.gguf"],
            args);
    }

    [Fact]
    public void ServerArgs_TaesdAlone_KeepsCheckpoint_AndExplicitRoleWins()
    {
        var taesdOnly = StableDiffusionPresetSettings.ServerArgs(Preset("sd15.safetensors", (StableDiffusionPresetSettings.Taesd, "taesd.safetensors")));
        Assert.Equal("-m", taesdOnly[0]);

        var forced = StableDiffusionPresetSettings.ServerArgs(Preset("z.safetensors", (StableDiffusionPresetSettings.ModelRole, "diffusion")));
        Assert.Equal("--diffusion-model", forced[0]);
    }

    [Fact]
    public void ServerArgs_GenerationAndPerformanceSettings()
    {
        var args = StableDiffusionPresetSettings.ServerArgs(Preset("m.gguf",
            (StableDiffusionPresetSettings.Width, "1024"),
            (StableDiffusionPresetSettings.Height, "768"),
            (StableDiffusionPresetSettings.Steps, "8"),
            (StableDiffusionPresetSettings.CfgScale, "1.5"),
            (StableDiffusionPresetSettings.SamplingMethod, "euler"),
            (StableDiffusionPresetSettings.Seed, "-1"),
            (StableDiffusionPresetSettings.NegativePrompt, "blurry, bad"),
            (StableDiffusionPresetSettings.FlashAttention, "ON"),
            (StableDiffusionPresetSettings.OffloadToCpu, "true"),
            (StableDiffusionPresetSettings.VaeTiling, "false"),
            (StableDiffusionPresetSettings.ExtraArgs, "--max-vram 10 --backend \"te=cpu\"")));

        Assert.Equal(
        [
            "-m", "m.gguf", "-W", "1024", "-H", "768", "--steps", "8", "--cfg-scale", "1.5", "--sampling-method", "euler",
            "-s", "-1", "-n", "blurry, bad", "--flash-attn", "on", "--offload-to-cpu", "--max-vram", "10", "--backend", "te=cpu",
        ], args);
    }

    [Theory]
    [InlineData(StableDiffusionPresetSettings.Steps, "0")]
    [InlineData(StableDiffusionPresetSettings.Width, "wide")]
    [InlineData(StableDiffusionPresetSettings.CfgScale, "-2")]
    [InlineData(StableDiffusionPresetSettings.FlashAttention, "maybe")]
    public void ServerArgs_RejectsInvalidSettings(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => StableDiffusionPresetSettings.ServerArgs(Preset("m.gguf", (key, value))));
    }

    [Fact]
    public void ServerArgs_NoModel_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => StableDiffusionPresetSettings.ServerArgs(Preset("")));
    }

    [Fact]
    public void MemoryEstimate_SumsEveryModelFile_OrLargestWhenOffloading()
    {
        const long Mb = 1024 * 1024;
        var sizes = new Dictionary<string, long> { ["dit"] = 10_000 * Mb, ["vae"] = 300 * Mb, ["te"] = 5_000 * Mb };
        long? Size(string path) => sizes.TryGetValue(path, out var s) ? s : null;

        var preset = Preset("dit", (StableDiffusionPresetSettings.Vae, "vae"), (StableDiffusionPresetSettings.Llm, "te"));
        long all = PresetMemoryEstimator.EstimateBytes(preset, null, Size, ServerEngine.StableDiffusionCpp);
        Assert.Equal(15_300 * Mb + (long)(15_300 * Mb * 0.05) + 2048 * Mb, all);

        preset.EngineSettings[StableDiffusionPresetSettings.OffloadToCpu] = "true";
        long offloaded = PresetMemoryEstimator.EstimateBytes(preset, null, Size, ServerEngine.StableDiffusionCpp);
        Assert.Equal(10_000 * Mb + (long)(10_000 * Mb * 0.05) + 2048 * Mb, offloaded);
    }

    // --- release assets ---

    private static readonly IReadOnlyList<GitHubReleaseAsset> Assets = new[]
    {
        "cudart-sd-bin-win-cu12-x64.zip",
        "sd-master-e0ed724-bin-Darwin-macOS-26.6.2-arm64.zip",
        "sd-master-e0ed724-bin-Linux-Ubuntu-24.04-x86_64-rocm-10.1.0.zip",
        "sd-master-e0ed724-bin-Linux-Ubuntu-24.04-x86_64-vulkan.zip",
        "sd-master-e0ed724-bin-Linux-Ubuntu-24.04-x86_64.zip",
        "sd-master-e0ed724-bin-win-cpu-x64.zip",
        "sd-master-e0ed724-bin-win-cuda12-x64.zip",
        "sd-master-e0ed724-bin-win-rocm-10.1.0-x64.zip",
        "sd-master-e0ed724-bin-win-vulkan-x64.zip",
    }.Select(n => new GitHubReleaseAsset { Name = n }).ToList();

    [Theory]
    [InlineData(BackendType.Cpu, "win", "x64", "sd-master-e0ed724-bin-win-cpu-x64.zip")]
    [InlineData(BackendType.Vulkan, "win", "x64", "sd-master-e0ed724-bin-win-vulkan-x64.zip")]
    [InlineData(BackendType.Hip, "win", "x64", "sd-master-e0ed724-bin-win-rocm-10.1.0-x64.zip")]
    [InlineData(BackendType.Cpu, "ubuntu", "x64", "sd-master-e0ed724-bin-Linux-Ubuntu-24.04-x86_64.zip")]
    [InlineData(BackendType.Vulkan, "ubuntu", "x64", "sd-master-e0ed724-bin-Linux-Ubuntu-24.04-x86_64-vulkan.zip")]
    [InlineData(BackendType.Hip, "ubuntu", "x64", "sd-master-e0ed724-bin-Linux-Ubuntu-24.04-x86_64-rocm-10.1.0.zip")]
    [InlineData(BackendType.Metal, "macos", "arm64", "sd-master-e0ed724-bin-Darwin-macOS-26.6.2-arm64.zip")]
    public void ReleaseProfile_PicksTheBackendsArchive(BackendType backend, string os, string arch, string expected)
    {
        var picked = SdCppReleaseProfile.Instance.Resolve(Assets, backend, os, arch);

        Assert.Equal([expected], picked.Select(a => a.Name));
    }

    [Fact]
    public void ReleaseProfile_WindowsCuda_AddsTheCudaRuntime()
    {
        var picked = SdCppReleaseProfile.Instance.Resolve(Assets, BackendType.Cuda, "win", "x64");

        Assert.Equal(["sd-master-e0ed724-bin-win-cuda12-x64.zip", "cudart-sd-bin-win-cu12-x64.zip"], picked.Select(a => a.Name));
    }

    [Fact]
    public void ReleaseProfile_UnpublishedTargets_AreUnavailable()
    {
        Assert.False(SdCppReleaseProfile.Instance.IsAvailable(Assets, BackendType.Cuda, "ubuntu", "x64"));
        Assert.False(SdCppReleaseProfile.Instance.IsAvailable(Assets, BackendType.Sycl, "win", "x64"));
        Assert.False(SdCppReleaseProfile.Instance.IsAvailable(Assets, BackendType.Cpu, "win", "arm64"));
        Assert.Throws<InvalidOperationException>(() => SdCppReleaseProfile.Instance.Resolve(Assets, BackendType.Cuda, "ubuntu", "x64"));
    }

    [Theory]
    [InlineData("master-999-e0ed724", true)]
    [InlineData("master-1-abcdef0", true)]
    [InlineData("b8000", false)]
    [InlineData("nightly", false)]
    public void ReleaseProfile_InstallableTags(string tag, bool installable)
    {
        Assert.Equal(installable, SdCppReleaseProfile.Instance.IsInstallableRelease(tag));
    }

    // --- request translation ---

    [Fact]
    public void ImageRequest_PlainOpenAiBody_DropsUnsupportedFields()
    {
        var sent = JsonNode.Parse(StableDiffusionCppProvider.ToSdCppImageRequest(
            """{"model":"flux","prompt":"a cat","n":2,"size":"1024x1024","response_format":"url","quality":"hd"}"""))!.AsObject();

        Assert.False(sent.ContainsKey("model"));
        Assert.False(sent.ContainsKey("response_format"));
        Assert.False(sent.ContainsKey("quality"));
        Assert.Equal("a cat", sent["prompt"]!.GetValue<string>());
        Assert.Equal(2, sent["n"]!.GetValue<int>());
        Assert.Equal("1024x1024", sent["size"]!.GetValue<string>());
    }

    [Fact]
    public void ImageRequest_ExtensionFields_BecomeExtraArgs_AndClientBlockWins()
    {
        var sent = JsonNode.Parse(StableDiffusionCppProvider.ToSdCppImageRequest(
            """{"model":"flux","prompt":"a cat <sd_cpp_extra_args>{\"sample_params\":{\"sample_steps\":4}}</sd_cpp_extra_args>","negative_prompt":"dog","steps":30,"cfg_scale":1.0,"seed":7}"""))!.AsObject();

        var prompt = sent["prompt"]!.GetValue<string>();
        Assert.StartsWith("a cat <sd_cpp_extra_args>", prompt);
        Assert.EndsWith("</sd_cpp_extra_args>", prompt);
        foreach (var field in new[] { "negative_prompt", "steps", "cfg_scale", "seed" })
            Assert.False(sent.ContainsKey(field));

        var json = prompt["a cat <sd_cpp_extra_args>".Length..^"</sd_cpp_extra_args>".Length];
        var extra = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("dog", extra["negative_prompt"]!.GetValue<string>());
        Assert.Equal(7, extra["seed"]!.GetValue<int>());
        Assert.Equal(4, extra["sample_params"]!["sample_steps"]!.GetValue<int>());
        Assert.Equal(1.0, extra["sample_params"]!["guidance"]!["txt_cfg"]!.GetValue<double>());
    }
}
