using System.Globalization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Application.Pages.Features.Devices;

public class DevicesListModel : PageModel
{
    private readonly IComputeDeviceService _deviceService;

    public IReadOnlyList<ComputeDevice> Devices { get; set; } = new List<ComputeDevice>();

    public DevicesListModel(IComputeDeviceService deviceService)
    {
        _deviceService = deviceService;
    }

    public async Task OnGetAsync()
    {
        Devices = await _deviceService.GetDevicesAsync();
    }

    public async Task<IActionResult> OnPostToggleAsync([FromQuery] string instanceId, [FromQuery] bool enable)
    {
        var (success, error) = await _deviceService.SetDeviceEnabledAsync(instanceId, enable);
        if (!success)
            return BadRequest(error ?? "Failed to update device state.");

        return new OkResult();
    }

    public static string FormatBackend(BackendType backend) => backend switch
    {
        BackendType.Cuda => "CUDA",
        BackendType.Sycl => "SYCL",
        _ => backend.ToString(),
    };

    public static string FormatVram(long? vramBytes) => vramBytes is { } bytes
        ? (bytes / 1024d / 1024 / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " GB"
        : "—";
}
