using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Static description of a supported server engine: what it's called, which
/// <see cref="IBackendProvider"/> runs it, and how its server-level configuration is presented
/// and validated in the UI. One implementation per engine, registered in DI and surfaced through
/// <see cref="IEngineCatalog"/> — adding an engine means adding a descriptor + provider pair
/// rather than sprinkling engine checks through pages and services.
/// </summary>
public interface IEngineDescriptor
{
    /// <summary>The engine this descriptor describes.</summary>
    ServerEngine Engine { get; }

    /// <summary>Human-readable engine name (e.g. "llama.cpp").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Concrete <see cref="IBackendProvider"/> type, created per server instance via DI
    /// (constructor dependencies are resolved from the container).
    /// </summary>
    Type ProviderType { get; }

    /// <summary>
    /// Whether servers of this engine can bind to a managed build from the Engines page
    /// (<see cref="BackendConfig.EngineBuildId"/>). Managed builds are currently llama.cpp-only.
    /// </summary>
    bool SupportsManagedBuilds { get; }

    /// <summary>Label for the install folder field (<see cref="BackendConfig.InstallFolderPath"/>).</summary>
    string InstallFolderLabel { get; }

    /// <summary>Help text shown under the install folder field.</summary>
    string InstallFolderHelp { get; }

    /// <summary>Example install folder shown as the field placeholder.</summary>
    string InstallFolderPlaceholder { get; }

    /// <summary>
    /// Help text for a preset's model path when the preset belongs to a server of this engine —
    /// engines differ in what that path points at (a GGUF file, a run-config file, ...).
    /// </summary>
    string ModelPathHelp { get; }

    /// <summary>
    /// Checks that <paramref name="folderPath"/> looks like a usable install of this engine.
    /// Returns an error message to show the user, or null if it's fine.
    /// </summary>
    string? ValidateInstallFolder(string folderPath);
}
