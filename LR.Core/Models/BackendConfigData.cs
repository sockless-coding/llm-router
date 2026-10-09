namespace LR.Core.Models;

/// <summary>
/// DTO for creating or updating engine-specific backend configuration.
/// </summary>
public class BackendConfigData
{
    /// <summary>
    /// Path to the engine's install folder (see <see cref="BackendConfig.InstallFolderPath"/>).
    /// </summary>
    public string? InstallFolderPath { get; set; }

    /// <summary>
    /// Path to a companion application that should run alongside the server.
    /// For example, the SYCL VRAM keeper app on Windows when no display is connected.
    /// </summary>
    public string? CompanionAppPath { get; set; }

    /// <summary>
    /// Shell command to initialize the environment before starting server processes.
    /// For example, "C:\Program Files (x86)\Intel\oneAPI\setvars.bat" intel64 for SYCL backends on Windows.
    /// </summary>
    public string? EnvironmentSetupCommand { get; set; }

    /// <summary>
    /// Optional link to a managed <see cref="EngineBuild"/>. When set, the executable folder is
    /// resolved from that build and <see cref="InstallFolderPath"/> is only a fallback.
    /// </summary>
    public Guid? EngineBuildId { get; set; }
}
