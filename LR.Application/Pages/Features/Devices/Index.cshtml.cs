using System.Globalization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Application.Services;
using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.Devices;

public class DevicesListModel : PageModel
{
    private readonly IComputeDeviceService _deviceService;
    private readonly ISyclKeepAliveSettingsService _keepAliveSettings;
    private readonly SyclKeepAliveService _keepAlive;

    public IReadOnlyList<ComputeDevice> Devices { get; set; } = new List<ComputeDevice>();

    public SyclKeepAliveSettings KeepAliveSettings { get; set; } = new();

    public SyclKeepAliveStatus KeepAliveStatus { get; set; } = new(SyclKeepAliveState.Idle, "");

    public string BundledKeepAlivePath => _keepAlive.BundledExecutablePath;

    [BindProperty]
    public SyclKeepAliveMode KeepAliveMode { get; set; }

    [BindProperty]
    public string? KeepAliveEnvironmentSetupCommand { get; set; }

    [BindProperty]
    public string? KeepAliveExecutablePath { get; set; }

    public DevicesListModel(IComputeDeviceService deviceService, ISyclKeepAliveSettingsService keepAliveSettings, SyclKeepAliveService keepAlive)
    {
        _deviceService = deviceService;
        _keepAliveSettings = keepAliveSettings;
        _keepAlive = keepAlive;
    }

    public async Task OnGetAsync()
    {
        Devices = await _deviceService.GetDevicesAsync();
        KeepAliveSettings = await _keepAliveSettings.GetAsync();
        KeepAliveStatus = _keepAlive.Status;
    }

    public async Task<IActionResult> OnPostKeepAliveAsync(CancellationToken ct)
    {
        await _keepAliveSettings.SaveAsync(KeepAliveMode, KeepAliveEnvironmentSetupCommand ?? "", KeepAliveExecutablePath, ct);
        // Saving applies the settings now, which also clears a manual Stop.
        await _keepAlive.StartKeepAliveAsync(ct);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostKeepAliveStopAsync(CancellationToken ct)
    {
        await _keepAlive.StopKeepAliveAsync(ct);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostKeepAliveStartAsync(CancellationToken ct)
    {
        await _keepAlive.StartKeepAliveAsync(ct);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostKeepAliveRecheckAsync(CancellationToken ct)
    {
        await _keepAlive.ReevaluateAsync(ct);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync([FromQuery] string instanceId, [FromQuery] bool enable)
    {
        // Disabling an Intel card the keep-alive may be holding? Release it first (see RunDeviceChangeAsync).
        var device = (await _deviceService.GetDevicesAsync())
            .FirstOrDefault(d => string.Equals(d.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        var releaseGpu = !enable && device is { IsSyclGpu: true };

        var (success, error) = await _keepAlive.RunDeviceChangeAsync(releaseGpu, enabling: enable,
            () => _deviceService.SetDeviceEnabledAsync(instanceId, enable));
        if (!success)
            return BadRequest(error ?? "Failed to update device state.");

        return new OkResult();
    }

    public static string FormatBackend(BackendType backend) => backend switch
    {
        BackendType.Cuda => "CUDA",
        BackendType.Sycl => "SYCL",
        BackendType.Hip => "HIP / ROCm",
        BackendType.OpenCL => "OpenCL",
        BackendType.OpenVino => "OpenVINO",
        BackendType.Musa => "MUSA",
        BackendType.Cann => "CANN",
        _ => backend.ToString(),
    };

    public static string FormatVram(long? vramBytes) => vramBytes is { } bytes
        ? (bytes / 1024d / 1024 / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " GB"
        : "—";
}
