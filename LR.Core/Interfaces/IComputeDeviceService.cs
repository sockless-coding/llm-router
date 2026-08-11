using LR.Core.Models;

namespace LR.Core.Interfaces;

/// <summary>
/// Detects AI-compute-capable devices (GPUs/NPUs) and toggles their enabled state in the OS,
/// so a device (e.g. a secondary GPU only wanted for occasional SYCL/CUDA workloads) can be
/// left disabled in Device Manager until it's needed.
/// </summary>
public interface IComputeDeviceService
{
    Task<IReadOnlyList<ComputeDevice>> GetDevicesAsync(CancellationToken ct = default);

    Task<(bool Success, string? Error)> SetDeviceEnabledAsync(string instanceId, bool enabled, CancellationToken ct = default);
}
