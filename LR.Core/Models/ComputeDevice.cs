namespace LR.Core.Models;

public enum ComputeDeviceKind
{
    Gpu,
    Npu
}

/// <summary>
/// An AI-compute-capable hardware device (GPU or NPU) detected via the OS device tree.
/// </summary>
public class ComputeDevice
{
    /// <summary>PnP device instance ID (e.g. "PCI\VEN_8086&amp;DEV_...\..."), used to enable/disable the device.</summary>
    public required string InstanceId { get; set; }
    public required string Name { get; set; }
    public string? Manufacturer { get; set; }
    public ComputeDeviceKind Kind { get; set; }
    public bool IsEnabled { get; set; }
    public string? StatusText { get; set; }

    /// <summary>Total VRAM in bytes, when it could be read from the driver. Null if unknown.</summary>
    public long? VramBytes { get; set; }

    /// <summary>
    /// Backend types this device can likely serve, inferred from its vendor and which compute
    /// runtimes (CUDA/Vulkan/oneAPI) are installed on the machine — not a guarantee llama.cpp
    /// will run on it, just a hint for picking a BackendConfig.GpuBackendType.
    /// </summary>
    public IReadOnlyList<BackendType> SupportedBackends { get; set; } = Array.Empty<BackendType>();

    /// <summary>
    /// Other inference runtimes this device likely supports that aren't a llama.cpp
    /// BackendConfig.GpuBackendType option (e.g. "OpenVINO" — relevant for Intel GPUs/NPUs).
    /// Purely informational.
    /// </summary>
    public IReadOnlyList<string> OtherCapabilities { get; set; } = Array.Empty<string>();
}
