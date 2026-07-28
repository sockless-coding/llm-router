using System.ComponentModel.DataAnnotations;

namespace LR.Core.Models;

/// <summary>
/// The GPU compute backend that a llama.cpp build was compiled for.
/// Used by routing rules to match requests against preferred backends,
/// and stored per-server in BackendConfig.GpuBackendType.
/// This is NOT the server engine — see ServerEngine for that (llama.cpp, Ollama, etc.).
/// </summary>
public enum BackendType
{
    Unknown = 0,
    Cuda = 1,
    Vulkan = 2,
    Sycl = 3,
    Cpu = 4,
}
