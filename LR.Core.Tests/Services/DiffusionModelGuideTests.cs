using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Core.Tests.Services;

public class DiffusionModelGuideTests
{
    [Theory]
    [InlineData("comfyanonymous/flux_text_encoders", "clip_l.safetensors", DiffusionFileRole.ClipL)]
    [InlineData("comfyanonymous/flux_text_encoders", "t5xxl_fp8_e4m3fn.safetensors", DiffusionFileRole.T5xxl)]
    [InlineData("black-forest-labs/FLUX.1-schnell", "ae.safetensors", DiffusionFileRole.Vae)]
    [InlineData("black-forest-labs/FLUX.1-schnell", "flux1-schnell.safetensors", DiffusionFileRole.Model)]
    [InlineData("Comfy-Org/z_image_turbo", "split_files/vae/ae.safetensors", DiffusionFileRole.Vae)]
    [InlineData("Comfy-Org/z_image_turbo", "split_files/text_encoders/qwen_3_4b.safetensors", DiffusionFileRole.Llm)]
    [InlineData("Comfy-Org/z_image_turbo", "split_files/diffusion_models/z_image_turbo_bf16.safetensors", DiffusionFileRole.Model)]
    [InlineData("Comfy-Org/Wan_2.1_ComfyUI_repackaged", "split_files/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors", DiffusionFileRole.T5xxl)]
    [InlineData("Comfy-Org/Wan_2.1_ComfyUI_repackaged", "split_files/clip_vision/clip_vision_h.safetensors", DiffusionFileRole.Other)]
    [InlineData("stabilityai/stable-diffusion-xl-base-1.0", "sd_xl_base_1.0_0.9vae.safetensors", DiffusionFileRole.Model)]
    [InlineData("stabilityai/stable-diffusion-xl-base-1.0", "vae/diffusion_pytorch_model.safetensors", DiffusionFileRole.Vae)]
    [InlineData("stabilityai/stable-diffusion-xl-base-1.0", "unet/diffusion_pytorch_model.safetensors", DiffusionFileRole.Other)]
    [InlineData("stabilityai/stable-diffusion-xl-base-1.0", "text_encoder_2/model.safetensors", DiffusionFileRole.TextEncoder)]
    [InlineData("black-forest-labs/FLUX.1-dev", "transformer/diffusion_pytorch_model-00001-of-00003.safetensors", DiffusionFileRole.Other)]
    [InlineData("madebyollin/taesdxl", "diffusion_pytorch_model.safetensors", DiffusionFileRole.Taesd)]
    [InlineData("madebyollin/sdxl-vae-fp16-fix", "diffusion_pytorch_model.safetensors", DiffusionFileRole.Vae)]
    [InlineData("madebyollin/sdxl-vae-fp16-fix", "sdxl_vae.safetensors", DiffusionFileRole.Vae)]
    [InlineData("stabilityai/sd-vae-ft-mse-original", "vae-ft-mse-840000-ema-pruned.safetensors", DiffusionFileRole.Vae)]
    [InlineData("someone/loras", "pixel_art_lora.safetensors", DiffusionFileRole.Lora)]
    [InlineData("unsloth/Qwen2.5-VL-7B-Instruct-GGUF", "mmproj-F16.gguf", DiffusionFileRole.Other)]
    public void ClassifiesFiles(string repoId, string path, DiffusionFileRole expected) =>
        Assert.Equal(expected, DiffusionModelGuide.ClassifyFile(repoId, path));

    [Theory]
    [InlineData("city96/FLUX.1-dev-gguf", "flux")]
    [InlineData("comfyanonymous/flux_text_encoders", "flux")]
    [InlineData("black-forest-labs/FLUX.2-dev", null)]
    [InlineData("Comfy-Org/z_image_turbo", "z-image")]
    [InlineData("leejet/Z-Image-Turbo-GGUF", "z-image")]
    [InlineData("QuantStack/Qwen-Image-GGUF", "qwen-image")]
    [InlineData("unsloth/Qwen2.5-VL-7B-Instruct-GGUF", null)]
    [InlineData("Comfy-Org/Wan_2.1_ComfyUI_repackaged", "wan")]
    [InlineData("stabilityai/stable-diffusion-3.5-large", "sd3")]
    [InlineData("stabilityai/stable-diffusion-xl-base-1.0", "sdxl")]
    [InlineData("stabilityai/sdxl-turbo", "sdxl")]
    [InlineData("stable-diffusion-v1-5/stable-diffusion-v1-5", "sd1")]
    [InlineData("stabilityai/stable-diffusion-2-1", "sd1")]
    [InlineData("bartowski/Llama-3.2-3B-Instruct-GGUF", null)]
    public void DetectsFamilyFromRepo(string repoId, string? expected) =>
        Assert.Equal(expected, DiffusionModelGuide.DetectFamily(repoId)?.Id);

    [Fact]
    public void DetectsFamilyFromGgufArchitecture()
    {
        var local = new LocalModel { FilePath = Path.Combine("lib", "my-model-Q4.gguf"), Architecture = "flux" };
        Assert.Equal("flux", DiffusionModelGuide.DetectFamily(local)?.Id);
    }

    [Fact]
    public void SuggestsFluxComponentsFromLibrary()
    {
        var main = Hf("city96/FLUX.1-dev-gguf", "flux1-dev-Q4_K_S.gguf");
        var library = new[]
        {
            main,
            Hf("comfyanonymous/flux_text_encoders", "clip_l.safetensors"),
            Hf("comfyanonymous/flux_text_encoders", "t5xxl_fp16.safetensors"),
            Hf("Comfy-Org/z_image_turbo", "split_files/vae/ae.safetensors"),
            Hf("Comfy-Org/Qwen-Image_ComfyUI", "split_files/vae/qwen_image_vae.safetensors"),
        };

        var suggestion = DiffusionModelGuide.ForPreset(main, library);

        Assert.NotNull(suggestion);
        Assert.Equal("flux", suggestion.Family.Id);
        Assert.Equal(StableDiffusionPresetSettings.RoleDiffusion, suggestion.Family.ModelRole);
        Assert.Equal(library[1].FilePath, suggestion.Files[StableDiffusionPresetSettings.ClipL]);
        Assert.Equal(library[2].FilePath, suggestion.Files[StableDiffusionPresetSettings.T5xxl]);
        Assert.Equal(library[3].FilePath, suggestion.Files[StableDiffusionPresetSettings.Vae]);
        Assert.Empty(suggestion.Missing);
    }

    [Fact]
    public void ReportsMissingComponentsAndSkipsChatModels()
    {
        var main = Hf("leejet/Z-Image-Turbo-GGUF", "z_image_turbo-Q4_K.gguf");
        var library = new[] { main, Hf("unsloth/Qwen3-30B-A3B-GGUF", "Qwen3-30B-A3B-Q4_K_M.gguf") };

        var suggestion = DiffusionModelGuide.ForPreset(main, library);

        Assert.NotNull(suggestion);
        Assert.Empty(suggestion.Files);
        Assert.Equal(
            [StableDiffusionPresetSettings.Vae, StableDiffusionPresetSettings.Llm],
            suggestion.Missing.Select(p => p.SettingKey));
    }

    [Fact]
    public void MatchesLlmEncoderForZImage()
    {
        var main = Hf("leejet/Z-Image-Turbo-GGUF", "z_image_turbo-Q4_K.gguf");
        var encoder = Hf("unsloth/Qwen3-4B-Instruct-2507-GGUF", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf");

        var suggestion = DiffusionModelGuide.ForPreset(main, [main, encoder]);

        Assert.Equal(encoder.FilePath, suggestion!.Files[StableDiffusionPresetSettings.Llm]);
    }

    [Fact]
    public void ComponentsAndChatModelsGetNoPresetSuggestion()
    {
        var vae = Hf("black-forest-labs/FLUX.1-schnell", "ae.safetensors");
        var llm = Hf("bartowski/Llama-3.2-3B-Instruct-GGUF", "Llama-3.2-3B-Instruct-Q4_K_M.gguf");

        Assert.Null(DiffusionModelGuide.ForPreset(vae, [vae]));
        Assert.Null(DiffusionModelGuide.ForPreset(llm, [llm]));
    }

    [Fact]
    public void RepoGuideFindsPartsInRepoAndLibrary()
    {
        var clipL = Hf("comfyanonymous/flux_text_encoders", "clip_l.safetensors");

        var guide = DiffusionModelGuide.ForRepo(
            "city96/FLUX.1-dev-gguf", ["flux1-dev-Q4_K_S.gguf", "flux1-dev-Q8_0.gguf"], [clipL]);

        Assert.NotNull(guide);
        var parts = guide.Value.Parts.ToDictionary(p => p.Part.SettingKey ?? "model");
        Assert.Equal(["flux1-dev-Q4_K_S.gguf", "flux1-dev-Q8_0.gguf"], parts["model"].RepoFiles);
        Assert.Same(clipL, parts[StableDiffusionPresetSettings.ClipL].InLibrary);
        Assert.Null(parts[StableDiffusionPresetSettings.Vae].InLibrary);
        Assert.Empty(parts[StableDiffusionPresetSettings.Vae].RepoFiles);
    }

    private static LocalModel Hf(string repoId, string filename) => new()
    {
        Id = Guid.NewGuid(),
        Name = Path.ChangeExtension(filename, null),
        FilePath = Path.Combine("lib", repoId.Replace('/', '_'), filename),
        Source = ModelSource.HuggingFace,
        HfRepoId = repoId,
        HfFilename = filename,
    };
}
