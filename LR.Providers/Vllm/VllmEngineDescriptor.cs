using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Providers.Vllm;

public class VllmEngineDescriptor : IEngineDescriptor
{
    public ServerEngine Engine => ServerEngine.Vllm;

    public string DisplayName => "vLLM";

    public EngineModality Modality => EngineModality.Text;

    public string? PresetFormSection => "section-vllm";

    public Type ProviderType => typeof(VllmProvider);

    public bool SupportsManagedBuilds => true;

    public string InstallFolderLabel => "vLLM Folder Path";

    public string InstallFolderHelp =>
        "A folder holding a Python environment with vLLM (.venv/bin/vllm). On Windows, vLLM runs in WSL, so use the distro's path " +
        @"(\\wsl.localhost\Ubuntu\home\you\vllm), or a native venv of the community vllm-windows fork (.venv\Scripts\vllm.exe). " +
        "Or pick an install from the Engines page, which can set one up and update it, including one for an Intel GPU, which runs in Docker Desktop.";

    public string InstallFolderPlaceholder => OperatingSystem.IsWindows() ? @"\\wsl.localhost\Ubuntu\home\you\vllm" : "/opt/vllm";

    public string ModelPathHelp =>
        "A Hugging Face model id (Qwen/Qwen3-8B), which vLLM downloads on first start into the Hugging Face cache of the machine it runs on, " +
        "or a model folder on that machine. Applying a recipe below fills this in.";

    public string? ValidateInstallFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return $"The folder '{folderPath}' does not exist.";
        if (!VllmLayout.IsInstall(folderPath))
            return $"'{folderPath}' doesn't hold a vLLM install (no .venv with a vllm executable).";
        return null;
    }
}
