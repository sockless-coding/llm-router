using LR.Core.Interfaces;
using LR.Core.Models;
using LR.Core.Services.EngineBuilds;

namespace LR.Providers.Strata;

public class StrataEngineDescriptor : IEngineDescriptor
{
    public ServerEngine Engine => ServerEngine.Strata;

    public string DisplayName => "Strata";

    public Type ProviderType => typeof(StrataProvider);

    public bool SupportsManagedBuilds => true;

    public string InstallFolderLabel => "Strata Folder Path";

    public string InstallFolderHelp =>
        "Path to your Strata checkout — the folder containing START-HERE.bat, serve\\server.py and the .venv " +
        "its setup creates. Run Strata's own setup there first (START-HERE.bat / ./setup.sh) to install the " +
        "engine and download a model. Or pick a Strata install tracked on the Engines page, which can also update it.";

    public string InstallFolderPlaceholder => @"C:\Strata";

    public string ModelPathHelp =>
        "Pick a Qwen3.8-Flash-Next GGUF from the library (ISTA-DASLab's GSQ-RCO quants, Swift 1.5, the Coder, or Unsloth's) — " +
        "the first start prepares it for Strata using the Strata settings below. A run config made by Strata's own " +
        "setup (strata-<model>.json) also works here, as it is.";

    public string? ValidateInstallFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return $"The folder '{folderPath}' does not exist.";

        if (!File.Exists(Path.Combine(folderPath, StrataLayout.ServerScriptRelativePath)))
            return $"'{folderPath}' doesn't look like a Strata checkout (no {StrataLayout.ServerScriptRelativePath}).";

        if (!File.Exists(Path.Combine(folderPath, StrataLayout.PythonRelativePath)))
            return $"Strata's Python environment ({StrataLayout.PythonRelativePath}) is missing — run {StrataLayout.FirstTimeSetupCommand} in '{folderPath}' first.";

        return null;
    }
}
