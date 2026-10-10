using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LR.Core.Models;

/// <summary>
/// Engine-specific configuration for a server instance.
/// Stores paths, companion app settings, and other engine-specific options.
/// One BackendConfig exists per ServerInstance.
/// </summary>
[Table("BackendConfigs")]
public class BackendConfig
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The server instance this config belongs to.
    /// </summary>
    [Required, ForeignKey(nameof(ServerInstance))]
    public Guid ServerInstanceId { get; set; }

    /// <summary>
    /// Path to the engine's install folder — for llama.cpp the folder containing
    /// <c>llama-server</c> (each GPU backend build in its own folder); for Strata the repository
    /// checkout containing <c>serve/server.py</c> and <c>.venv</c>. See
    /// <see cref="LR.Core.Interfaces.IEngineDescriptor.InstallFolderHelp"/> per engine.
    /// </summary>
    [MaxLength(1024)]
    public string? InstallFolderPath { get; set; }

    /// <summary>
    /// The GPU backend type this llama.cpp build was compiled for.
    /// Derived from the executable folder or auto-detected at startup.
    /// </summary>
    public BackendType? GpuBackendType { get; set; }

    /// <summary>
    /// Optional link to a managed <see cref="EngineBuild"/>. When set, the server's executable
    /// folder is taken from that build's <see cref="EngineBuild.InstallPath"/> and
    /// <see cref="InstallFolderPath"/> acts only as a manual override/fallback. Nulled
    /// out (not cascade-deleted) if the referenced build is removed.
    /// </summary>
    [ForeignKey(nameof(EngineBuild))]
    public Guid? EngineBuildId { get; set; }

    /// <summary>Navigation: the managed build this server is bound to, if any.</summary>
    public EngineBuild? EngineBuild { get; set; }

    /// <summary>
    /// Path to a companion application that should run alongside the server.
    /// For example, the SYCL VRAM keeper app on Windows when no display is connected,
    /// or similar Vulkan helper processes.
    /// </summary>
    [MaxLength(1024)]
    public string? CompanionAppPath { get; set; }

    /// <summary>
    /// Shell command to initialize the environment before starting server processes.
    /// For example, "C:\Program Files (x86)\Intel\oneAPI\setvars.bat" intel64 for SYCL backends on Windows.
    /// This command is executed via cmd.exe /c so that .bat files and environment setup scripts work correctly,
    /// and the resulting environment variables are inherited by subsequent server/companion processes.
    /// </summary>
    [MaxLength(2048)]
    public string? EnvironmentSetupCommand { get; set; }

    /// <summary>
    /// Free-form key-value settings for engine-specific configuration.
    /// Stored as JSON in the database.
    /// </summary>
    [Column(TypeName = "TEXT")]
    public Dictionary<string, string> ExtraSettings { get; set; } = new();

    /// <summary>
    /// Navigation: parent server instance.
    /// </summary>
    public ServerInstance? ServerInstance { get; set; }
}
