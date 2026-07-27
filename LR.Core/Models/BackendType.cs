namespace LR.Core.Models;

/// <summary>
/// The inference backend type used by a server instance.
/// </summary>
public enum BackendType
{
    Unknown = 0,
    Cuda = 1,
    Vulkan = 2,
    Sycl = 3,
    Cpu = 4,
}
