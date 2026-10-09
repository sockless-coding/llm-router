using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Providers.LlamaCpp;

public class LlamaCppEngineDescriptor : IEngineDescriptor
{
    public ServerEngine Engine => ServerEngine.LlamaCpp;

    public string DisplayName => "llama.cpp";

    public Type ProviderType => typeof(LlamaCppProvider);

    public bool SupportsManagedBuilds => true;

    public string InstallFolderLabel => "Server Folder Path";

    public string InstallFolderHelp =>
        $"Path to the folder containing your llama.cpp server executable ({LlamaCppProvider.ServerExecutableName}). " +
        "Each GPU backend build should be in its own folder.";

    public string InstallFolderPlaceholder => @"C:\llamacpp\cuda-build";

    public string ModelPathHelp => "Path to the .gguf model file.";

    public string? ValidateInstallFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return $"The folder '{folderPath}' does not exist.";

        return null;
    }
}
