namespace LR.Core.Models;

/// <summary>
/// DTO for creating or updating engine-specific backend configuration.
/// </summary>
public class BackendConfigData
{
    /// <summary>
    /// Path to the folder containing the llama.cpp server executable (e.g., "llama-server").
    /// Each GPU backend build (CUDA, Vulkan, SYCL) should be in its own folder.
    /// </summary>
    public string? LlamaCppExecutableFolderPath { get; set; }

    /// <summary>
    /// Path to a companion application that should run alongside the server.
    /// For example, the SYCL VRAM keeper app on Windows when no display is connected.
    /// </summary>
    public string? CompanionAppPath { get; set; }
}
