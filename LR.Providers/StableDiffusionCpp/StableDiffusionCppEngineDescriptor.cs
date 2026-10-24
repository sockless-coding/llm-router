using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Providers.StableDiffusionCpp;

public class StableDiffusionCppEngineDescriptor : IEngineDescriptor
{
    public ServerEngine Engine => ServerEngine.StableDiffusionCpp;

    public string DisplayName => "stable-diffusion.cpp";

    public EngineModality Modality => EngineModality.Image;

    public string? PresetFormSection => "section-diffusion";

    public Type ProviderType => typeof(StableDiffusionCppProvider);

    public bool SupportsManagedBuilds => true;

    public string InstallFolderLabel => "Server Folder Path";

    public string InstallFolderHelp =>
        $"Path to the folder containing stable-diffusion.cpp's server executable ({StableDiffusionCppProvider.ServerExecutableName}). " +
        "Or pick a build installed from the Engines page.";

    public string InstallFolderPlaceholder => @"C:\stable-diffusion.cpp\cuda-build";

    public string ModelPathHelp =>
        "Path to the main model: a full checkpoint (SD 1.x/2.x/SDXL .safetensors/.gguf) or a standalone diffusion model " +
        "(Flux, SD3, Qwen-Image, Z-Image…) used with the VAE and text encoders set in the Diffusion section.";

    public string? ValidateInstallFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return $"The folder '{folderPath}' does not exist.";

        if (!File.Exists(Path.Combine(folderPath, StableDiffusionCppProvider.ServerExecutableName)))
            return $"'{folderPath}' doesn't contain {StableDiffusionCppProvider.ServerExecutableName}.";

        return null;
    }
}
