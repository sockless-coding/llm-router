using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Providers.TensorFold;

public class TensorFoldEngineDescriptor : IEngineDescriptor
{
    public ServerEngine Engine => ServerEngine.TensorFold;

    public string DisplayName => "TensorFold";

    public EngineModality Modality => EngineModality.Text;

    public string? PresetFormSection => "section-tensorfold";

    public Type ProviderType => typeof(TensorFoldProvider);

    public bool SupportsManagedBuilds => true;

    public string InstallFolderLabel => "TensorFold Folder Path";

    public string InstallFolderHelp =>
        "An unpacked TensorFold release (the folder holding bin/tensorfold-native and lib/) — releases are for Linux with an NVIDIA GPU " +
        "(CUDA 13, driver R580+) and Apple-silicon Macs. On Windows, a build from source: compile one from a recipe on the Engines page (CUDA, HIP), or point at your own zig-out\\native. " +
        "Or pick an install from the Engines page, which can install releases here or on an agent and update them.";

    public string InstallFolderPlaceholder => OperatingSystem.IsWindows() ? @"C:\TensorFold\zig-out\native" : "/opt/tensorfold-1.2.7-linux-x86_64";

    public string ModelPathHelp =>
        "A Hugging Face repo id (TensorFold/Qwen3.8-27B-MLX-4bit, optionally pinned with @revision), pulled into the Hugging Face cache of the " +
        "machine the server runs on at first start, or a complete checkpoint folder on that machine. TensorFold serves its own checkpoint " +
        "formats (MLX, NVFP4/FP8, EXL3…), not GGUF.";

    public string? ValidateInstallFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return $"The folder '{folderPath}' does not exist.";
        if (TensorFoldDocker.IsDockerFolder(folderPath))
            return TensorFoldDocker.IsInstall(folderPath) ? null : $"'{folderPath}' doesn't hold the TensorFold image's build files (Dockerfile). Install it from the Engines page.";
        if (!TensorFoldLayout.IsInstall(folderPath))
            return $"'{folderPath}' doesn't hold TensorFold ({TensorFoldLayout.BinFolder}/{TensorFoldLayout.ExecutableName(OperatingSystem.IsWindows())}).";
        return null;
    }
}
