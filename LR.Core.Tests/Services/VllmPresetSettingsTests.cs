using System.Text.Json;

using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Tests.Services;

public class VllmPresetSettingsTests
{
    private static ModelPreset Preset(string model, params (string Key, string Value)[] settings) => new()
    {
        ModelPath = model,
        EngineSettings = settings.ToDictionary(s => s.Key, s => s.Value),
    };

    /// <summary>Recorded from recipes.vllm.ai/Qwen/Qwen3-8B.json?hardware=rtx_5090&amp;strategy=single_node_tp&amp;features=tool_calling,reasoning.</summary>
    private const string RenderedRecipe = """
        {
          "hf_id": "Qwen/Qwen3-8B", "hardware": "xeon6", "strategy": "single_node_tp", "variant": "default",
          "node_count": 1, "deploy_type": "single_node", "env": {}, "docker_image": "vllm/vllm-openai-cpu:latest-x86_64",
          "command": "vllm serve Qwen/Qwen3-8B",
          "argv": ["vllm", "serve", "Qwen/Qwen3-8B", "--tensor-parallel-size", "1", "--max-num-batched-tokens", "16384",
                   "--gpu-memory-utilization", "0.8", "--enable-auto-tool-choice", "--tool-call-parser", "hermes", "--reasoning-parser", "qwen3"],
          "selection": {
            "hardware": "xeon6", "variant": "default", "strategy": "single_node_tp", "nodes": 1,
            "adjusted": [{ "param": "hardware", "requested": "rtx_5090", "used": "xeon6", "reason": "not available for variant `default`" }]
          },
          "page_url": "https://recipes.vllm.ai/Qwen/Qwen3-8B?hardware=rtx_5090"
        }
        """;

    private static VllmRecipeRendering Rendering(string json = RenderedRecipe)
    {
        using var doc = JsonDocument.Parse(json);
        return VllmRecipeClient.ParseRendering(doc.RootElement);
    }

    private static VllmRecipeRendering Rendering(string[] argv, Dictionary<string, string>? env = null, string deployType = "single_node") =>
        new("org/model", "h100", "single_node_tp", "default", deployType, argv, env ?? new(), [], null);

    [Fact]
    public void ServeArgs_NamesTheModelAndAlwaysServesUnderTheFixedName()
    {
        var args = VllmPresetSettings.ServeArgs(Preset("Qwen/Qwen3-8B"));

        Assert.Equal(["serve", "Qwen/Qwen3-8B", "--served-model-name", VllmLayout.ServedModelName], args);
    }

    [Fact]
    public void ServeArgs_MapsEverySetting_AndKeepsExtraTokensIntact()
    {
        const string json = """{"max_position_embeddings":131072,"rope":{"rope_type":"yarn"}}""";
        var args = VllmPresetSettings.ServeArgs(Preset("m/n",
            (VllmPresetSettings.MaxModelLen, "32k"),
            (VllmPresetSettings.GpuMemoryUtilization, "0.8"),
            (VllmPresetSettings.Dtype, "bfloat16"),
            (VllmPresetSettings.AutoToolChoice, "true"),
            (VllmPresetSettings.ToolCallParser, "hermes"),
            (VllmPresetSettings.ReasoningParser, "qwen3"),
            (VllmPresetSettings.TrustRemoteCode, "true"),
            (VllmPresetSettings.ExtraArgs, $"--hf-overrides\n{json}\n\n  --tensor-parallel-size  \n2")));

        Assert.Equal(
        [
            "serve", "m/n", "--served-model-name", VllmLayout.ServedModelName,
            "--max-model-len", "32k", "--gpu-memory-utilization", "0.8", "--dtype", "bfloat16",
            "--enable-auto-tool-choice", "--tool-call-parser", "hermes", "--reasoning-parser", "qwen3", "--trust-remote-code",
            "--hf-overrides", json, "--tensor-parallel-size", "2",
        ], args);
    }

    [Theory]
    [InlineData(VllmPresetSettings.MaxModelLen, "lots")]
    [InlineData(VllmPresetSettings.GpuMemoryUtilization, "1.5")]
    [InlineData(VllmPresetSettings.GpuMemoryUtilization, "0")]
    [InlineData(VllmPresetSettings.Dtype, "int4")]
    [InlineData(VllmPresetSettings.ExtraArgs, "--port\n9000")]
    [InlineData(VllmPresetSettings.ExtraArgs, "--host=0.0.0.0")]
    [InlineData(VllmPresetSettings.ExtraArgs, "--api-key\nx")]
    public void ServeArgs_RejectsBadValuesAndRouterOwnedFlags(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => VllmPresetSettings.ServeArgs(Preset("m/n", (key, value))));
    }

    [Fact]
    public void ServeArgs_RequiresAModel()
    {
        Assert.Throws<InvalidOperationException>(() => VllmPresetSettings.ServeArgs(Preset(" ")));
    }

    [Theory]
    [InlineData(@"F:\models\qwen", "/mnt/f/models/qwen")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\me\qwen", "/home/me/qwen")]
    [InlineData("Qwen/Qwen3-8B", "Qwen/Qwen3-8B")]
    public void ServeArgs_ConvertsModelFolderForWsl(string model, string expected)
    {
        Assert.Equal(expected, VllmPresetSettings.ServeArgs(Preset(model), targetIsWsl: true)[1]);
    }

    [Fact]
    public void EnvironmentOf_ParsesLines_KeepingEqualsSignsInValues()
    {
        var env = VllmPresetSettings.EnvironmentOf(Preset("m/n", (VllmPresetSettings.Environment, "A=1\n\nB=x=y")));

        Assert.Equal("1", env["A"]);
        Assert.Equal("x=y", env["B"]);
    }

    [Theory]
    [InlineData("NOEQUALS")]
    [InlineData("=value")]
    [InlineData("BAD NAME=1")]
    public void EnvironmentOf_RejectsMalformedLines(string line)
    {
        Assert.Throws<InvalidOperationException>(() => VllmPresetSettings.EnvironmentOf(Preset("m/n", (VllmPresetSettings.Environment, line))));
    }

    [Fact]
    public void FromRecipe_FillsFieldsAndPutsTheRestInExtraArgs()
    {
        var result = VllmPresetSettings.FromRecipe(Rendering().Argv, new Dictionary<string, string>());

        Assert.Equal("Qwen/Qwen3-8B", result.Model);
        Assert.Equal("0.8", result.Settings[VllmPresetSettings.GpuMemoryUtilization]);
        Assert.Equal("true", result.Settings[VllmPresetSettings.AutoToolChoice]);
        Assert.Equal("hermes", result.Settings[VllmPresetSettings.ToolCallParser]);
        Assert.Equal("qwen3", result.Settings[VllmPresetSettings.ReasoningParser]);
        Assert.Equal("--tensor-parallel-size\n1\n--max-num-batched-tokens\n16384", result.Settings[VllmPresetSettings.ExtraArgs]);
    }

    [Fact]
    public void FromRecipe_DropsRouterOwnedFlagsWithTheirValues_InBothSpellings()
    {
        var result = VllmPresetSettings.FromRecipe(
            ["vllm", "serve", "m/n", "--port", "8000", "--host=0.0.0.0", "--served-model-name", "a", "b", "--trust-remote-code", "--seed", "1"],
            new Dictionary<string, string>());

        Assert.Equal(["--port", "--host", "--served-model-name"], result.DroppedFlags);
        Assert.Equal("--seed\n1", result.Settings[VllmPresetSettings.ExtraArgs]);
        Assert.Equal("true", result.Settings[VllmPresetSettings.TrustRemoteCode]);
    }

    [Fact]
    public void FromRecipe_KeepsAJsonValueAsOneToken_RoundTrippingThroughServeArgs()
    {
        const string json = """{"max_position_embeddings":131072,"rope_parameters":{"factor":4.0}}""";
        var result = VllmPresetSettings.FromRecipe(["vllm", "serve", "m/n", "--hf-overrides", json], new Dictionary<string, string>());

        var args = VllmPresetSettings.ServeArgs(new ModelPreset { ModelPath = result.Model, EngineSettings = result.Settings.ToDictionary() });

        Assert.Equal(json, args[args.IndexOf("--hf-overrides") + 1]);
    }

    [Theory]
    [InlineData("LD_PRELOAD")]
    [InlineData("ld_library_path")]
    [InlineData("PYTHONPATH")]
    [InlineData("PATH")]
    [InlineData("HF_TOKEN")]
    [InlineData("HF_ENDPOINT")]
    [InlineData("WSLENV")]
    [InlineData("BAD;NAME")]
    public void FromRecipe_NeverTakesProtectedEnvironment(string name)
    {
        var result = VllmPresetSettings.FromRecipe(["vllm", "serve", "m/n"], new Dictionary<string, string> { [name] = "x", ["VLLM_OK"] = "1" });

        Assert.Equal([name], result.DroppedEnvironment);
        Assert.Equal("VLLM_OK=1", result.Settings[VllmPresetSettings.Environment]);
    }

    [Theory]
    [InlineData("docker", "run")]
    [InlineData("vllm", "bench")]
    public void FromRecipe_RefusesAnythingButVllmServe(string program, string verb)
    {
        Assert.Throws<InvalidOperationException>(() => VllmPresetSettings.FromRecipe([program, verb, "m/n"], new Dictionary<string, string>()));
    }

    [Fact]
    public void Apply_SaysWhenTheSiteSwappedTheRequestedHardware()
    {
        // The real behaviour: an RTX 5090 request quietly answered with a Xeon CPU recipe.
        var applied = VllmPresetSettings.Apply(Rendering(), details: null, ["tool_calling", "reasoning"]);

        Assert.Contains(applied.Warnings, w => w.Contains("rtx_5090") && w.Contains("xeon6"));
        Assert.Equal("xeon6", applied.Settings[VllmPresetSettings.RecipeHardware]);
        Assert.Equal("tool_calling,reasoning", applied.Settings[VllmPresetSettings.RecipeFeatures]);
    }

    [Fact]
    public void Apply_LeavesOutTheRustFrontend_SoAnthropicRequestsKeepWorking()
    {
        // Seen for real: with it, POST /v1/messages answers 501, and the Qwen/Qwen3.5-2B recipe sets it.
        var applied = VllmPresetSettings.Apply(
            Rendering(["vllm", "serve", "m/n"], new Dictionary<string, string> { [VllmPresetSettings.RustFrontendVariable] = "1", ["VLLM_OK"] = "1" }), null, null);

        Assert.Equal("VLLM_OK=1", applied.Settings[VllmPresetSettings.Environment]);
        Assert.Contains(applied.Warnings, w => w.Contains(VllmPresetSettings.RustFrontendVariable) && w.Contains("/v1/messages"));
    }

    [Fact]
    public void Apply_RefusesMultiNodeRecipes()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            VllmPresetSettings.Apply(Rendering(["vllm", "serve", "m/n"], deployType: "multi_node"), null, null));

        Assert.Contains("multi_node", ex.Message);
    }

    [Fact]
    public void Apply_WarnsAboutRemoteCodeGpuCountAndUnrunDependencies()
    {
        var details = new VllmRecipeDetails("org/model", "M", null, "0.20.0", null, [], "h100",
            [new VllmRecipeVariant("default", null, "bf16", 20, null, null, [])], [], "pip install thing");

        var applied = VllmPresetSettings.Apply(
            Rendering(["vllm", "serve", "org/model", "--trust-remote-code", "--tensor-parallel-size", "8"]), details, null);

        Assert.Contains(applied.Warnings, w => w.Contains("trust-remote-code"));
        Assert.Contains(applied.Warnings, w => w.Contains("8 GPUs"));
        Assert.Contains(applied.Warnings, w => w.Contains("pip install thing"));
        Assert.Contains(applied.Warnings, w => w.Contains("0.20.0"));
        Assert.Equal("20", applied.Settings[VllmPresetSettings.VramGb]);
    }

    [Fact]
    public void PresetMemoryEstimator_UsesTheRecipesVramMinimum()
    {
        var preset = Preset("m/n", (VllmPresetSettings.VramGb, "20"));

        long bytes = PresetMemoryEstimator.EstimateBytes(preset, null, _ => null, ServerEngine.Vllm);

        Assert.Equal(20L * 1024 * 1024 * 1024, bytes);
    }
}
