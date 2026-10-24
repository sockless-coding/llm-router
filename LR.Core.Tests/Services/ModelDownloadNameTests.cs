using LR.Core.Services;

namespace LR.Core.Tests.Services;

public class ModelDownloadNameTests
{
    [Theory]
    [InlineData("krea/Krea-2-Turbo", "turbo.safetensors", "Krea-2-Turbo")]
    [InlineData("someone/Some-Model", "model.safetensors", "Some-Model")]
    [InlineData("stabilityai/sdxl-base", "vae/diffusion_pytorch_model.safetensors", "sdxl-base/vae")]
    [InlineData("Qwen/Qwen3-8B-GGUF", "Qwen3-8B-Q4_K_M.gguf", "Qwen3-8B-Q4_K_M")]
    [InlineData("city96/FLUX.1-dev-gguf", "flux1-dev-Q4_K_S.gguf", "flux1-dev-Q4_K_S")]
    [InlineData("unsloth/Qwen3-235B-GGUF", "Q4_K_M/Qwen3-235B-Q4_K_M-00001-of-00003.gguf", "Q4_K_M/Qwen3-235B-Q4_K_M-00001-of-00003")]
    public void NamesDownloadsAfterTheModel(string repoId, string filename, string expected) =>
        Assert.Equal(expected, ModelDownloadService.DefaultName(repoId, filename));
}
