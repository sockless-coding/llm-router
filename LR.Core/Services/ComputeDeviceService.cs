using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

using Microsoft.Win32;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Enumerates GPU/NPU devices via WMI (Win32_PnPEntity) and toggles their enabled state via
/// pnputil.exe. Both operations require the process to run elevated (or as the LocalSystem
/// Windows Service) — pnputil reports a non-zero exit code otherwise, which is surfaced back
/// as the returned error message rather than thrown.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class ComputeDeviceService : IComputeDeviceService
{
    // Win32_PnPEntity.ConfigManagerErrorCode value Windows uses for "This device is disabled."
    // (CM_PROB_DISABLED) — the standard way to tell a disabled device apart from a working one.
    private const uint CM_PROB_DISABLED = 22;

    // "NPU" needs a word boundary — a plain Contains("NPU") also matches "USB Input Device"
    // (the letters "npu" fall consecutively inside "Input").
    [GeneratedRegex(@"\bNPU\b", RegexOptions.IgnoreCase)]
    private static partial Regex NpuWordRegex();

    private static readonly string[] NpuPhraseHints =
    {
        "AI Boost", "Neural Processor", "Neural Processing", "IPU 6"
    };

    // Matches the "VEN_xxxx&DEV_xxxx" hardware-id fragment out of a PNPDeviceID, used to find
    // the matching driver key under the display class registry (see TryGetVramBytes).
    [GeneratedRegex(@"VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}", RegexOptions.IgnoreCase)]
    private static partial Regex HardwareIdRegex();

    private const string DisplayClassRegistryKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public Task<IReadOnlyList<ComputeDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<IReadOnlyList<ComputeDevice>>(Array.Empty<ComputeDevice>());

        var devices = new List<ComputeDevice>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DeviceID, Manufacturer, PNPClass, ConfigManagerErrorCode, Status FROM Win32_PnPEntity");
        using var results = searcher.Get();

        foreach (var obj in results.Cast<ManagementBaseObject>())
        {
            using (obj)
            {
                var name = obj["Name"] as string;
                var deviceId = obj["DeviceID"] as string;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(deviceId))
                    continue;

                var pnpClass = obj["PNPClass"] as string;
                var kind = ClassifyDevice(name, pnpClass);
                if (kind is null)
                    continue;

                var errorCode = obj["ConfigManagerErrorCode"] is { } raw ? Convert.ToUInt32(raw) : 0u;
                var manufacturer = obj["Manufacturer"] as string;
                var vendor = DetectVendor(name, manufacturer);

                devices.Add(new ComputeDevice
                {
                    InstanceId = deviceId,
                    Name = name,
                    Manufacturer = manufacturer,
                    Kind = kind.Value,
                    IsEnabled = errorCode != CM_PROB_DISABLED,
                    StatusText = obj["Status"] as string,
                    VramBytes = kind == ComputeDeviceKind.Gpu ? TryGetVramBytes(deviceId) : null,
                    SupportedBackends = GetSupportedBackends(vendor, kind.Value),
                    OtherCapabilities = GetOtherCapabilities(vendor),
                });
            }
        }

        IReadOnlyList<ComputeDevice> ordered = devices
            .OrderBy(d => d.Kind)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult(ordered);
    }

    public async Task<(bool Success, string? Error)> SetDeviceEnabledAsync(string instanceId, bool enabled, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return (false, "Device control is only supported on Windows.");

        if (string.IsNullOrWhiteSpace(instanceId))
            return (false, "Missing device instance ID.");

        var psi = new ProcessStartInfo("pnputil.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(enabled ? "/enable-device" : "/disable-device");
        psi.ArgumentList.Add(instanceId);

        using var process = Process.Start(psi);
        if (process is null)
            return (false, "Failed to start pnputil.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode == 0)
            return (true, null);

        var message = !string.IsNullOrWhiteSpace(stderr) ? stderr : stdout;
        return (false, string.IsNullOrWhiteSpace(message)
            ? $"pnputil exited with code {process.ExitCode}. This usually means the app isn't running elevated."
            : message.Trim());
    }

    private static ComputeDeviceKind? ClassifyDevice(string name, string? pnpClass)
    {
        if (string.Equals(pnpClass, "Display", StringComparison.OrdinalIgnoreCase))
            return ComputeDeviceKind.Gpu;

        if (string.Equals(pnpClass, "Neural processors", StringComparison.OrdinalIgnoreCase))
            return ComputeDeviceKind.Npu;

        if (NpuWordRegex().IsMatch(name) || Array.Exists(NpuPhraseHints, hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            return ComputeDeviceKind.Npu;

        return null;
    }

    private enum GpuVendor { Unknown, Nvidia, Amd, Intel }

    // "ATI" needs a word boundary — a plain Contains("ATI") also matches "Intel Corporation"
    // (the letters "ati" fall consecutively inside "Corporation"), the same class of false
    // positive as the earlier "NPU"-inside-"Input" bug.
    [GeneratedRegex(@"\bATI\b", RegexOptions.IgnoreCase)]
    private static partial Regex AtiWordRegex();

    private static GpuVendor DetectVendor(string name, string? manufacturer)
    {
        var text = $"{name} {manufacturer}";
        if (text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return GpuVendor.Nvidia;
        if (text.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return GpuVendor.Intel;
        if (text.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
            AtiWordRegex().IsMatch(text))
            return GpuVendor.Amd;
        return GpuVendor.Unknown;
    }

    /// <summary>
    /// Win32_VideoController.AdapterRAM is a 32-bit value that overflows/clamps for cards with
    /// more than ~4GB VRAM, so the accurate value has to come from the display driver's own
    /// registry key instead — modern NVIDIA/AMD/Intel drivers all write a QWORD there.
    /// </summary>
    private static long? TryGetVramBytes(string instanceId)
    {
        var hwIdMatch = HardwareIdRegex().Match(instanceId);
        if (!hwIdMatch.Success)
            return null;

        using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassRegistryKey);
        if (classKey is null)
            return null;

        foreach (var subKeyName in classKey.GetSubKeyNames())
        {
            using var subKey = classKey.OpenSubKey(subKeyName);
            var matchingDeviceId = subKey?.GetValue("MatchingDeviceId") as string;
            if (matchingDeviceId is null || !matchingDeviceId.Contains(hwIdMatch.Value, StringComparison.OrdinalIgnoreCase))
                continue;

            if (subKey!.GetValue("HardwareInformation.qwMemorySize") is long vram && vram > 0)
                return vram;
        }

        return null;
    }

    private static bool RuntimeDllInstalled(string fileName) =>
        File.Exists(Path.Combine(Environment.SystemDirectory, fileName));

    /// <summary>
    /// Infers which llama.cpp backend(s) a device could likely serve, from its vendor plus
    /// whether the matching compute runtime is installed system-wide. This is a hint for picking
    /// a BackendConfig.GpuBackendType, not a guarantee — it doesn't verify a working build exists.
    /// </summary>
    private static IReadOnlyList<BackendType> GetSupportedBackends(GpuVendor vendor, ComputeDeviceKind kind)
    {
        if (kind != ComputeDeviceKind.Gpu)
            return Array.Empty<BackendType>();

        var vulkanInstalled = RuntimeDllInstalled("vulkan-1.dll");
        var backends = new List<BackendType>();

        switch (vendor)
        {
            case GpuVendor.Nvidia:
                if (RuntimeDllInstalled("nvcuda.dll"))
                    backends.Add(BackendType.Cuda);
                if (vulkanInstalled)
                    backends.Add(BackendType.Vulkan);
                break;
            case GpuVendor.Intel:
                // The Level Zero *loader* (ze_loader.dll) is normally app-bundled rather than
                // installed system-wide, so also accept the Intel GPU ICD (ze_intel_gpu64.dll)
                // that the graphics driver itself installs — either one means Level Zero/oneAPI
                // execution is available on this machine.
                if (RuntimeDllInstalled("ze_loader.dll") || RuntimeDllInstalled("ze_intel_gpu64.dll"))
                    backends.Add(BackendType.Sycl);
                if (vulkanInstalled)
                    backends.Add(BackendType.Vulkan);
                break;
            case GpuVendor.Amd:
            case GpuVendor.Unknown:
            default:
                if (vulkanInstalled)
                    backends.Add(BackendType.Vulkan);
                break;
        }

        return backends;
    }

    /// <summary>
    /// OpenVINO isn't installed by the GPU/NPU driver the way CUDA/Vulkan/Level Zero are — it's
    /// normally deployed with the app or a Python environment — so this looks for the runtime
    /// DLL, the toolkit's own environment variable, and its default install folder rather than a
    /// single well-known System32 file. Only reported for Intel hardware since that's OpenVINO's
    /// native target (CPU/GPU/NPU); it can run on other vendors' GPUs too, but far less commonly.
    /// </summary>
    private static IReadOnlyList<string> GetOtherCapabilities(GpuVendor vendor)
    {
        if (vendor != GpuVendor.Intel || !IsOpenVinoInstalled())
            return Array.Empty<string>();

        return new[] { "OpenVINO" };
    }

    private static bool IsOpenVinoInstalled()
    {
        if (RuntimeDllInstalled("openvino.dll"))
            return true;

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INTEL_OPENVINO_DIR")))
            return true;

        foreach (var programFiles in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            var intelDir = Path.Combine(programFiles, "Intel");
            if (Directory.Exists(intelDir) && Directory.GetDirectories(intelDir, "openvino*").Length > 0)
                return true;
        }

        return false;
    }
}
