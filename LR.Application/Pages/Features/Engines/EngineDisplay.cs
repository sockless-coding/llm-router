using LR.Core.Models;

namespace LR.Application.Pages.Features.Engines;

/// <summary>Formatting shared by the engine tabs that list downloaded/compiled builds.</summary>
public static class EngineDisplay
{
    public static string FormatSize(long? bytes)
    {
        if (bytes is null) return "—";
        double gb = bytes.Value / 1024.0 / 1024.0 / 1024.0;
        return gb >= 1 ? $"{gb:F1} GB" : $"{bytes.Value / 1024.0 / 1024.0:F0} MB";
    }

    public static string BackendBadge(BackendType b) => b switch
    {
        BackendType.Cuda => "bg-neon-green/10 text-neon-green border-neon-green/20",
        BackendType.Vulkan => "bg-neon-magenta/10 text-neon-magenta border-neon-magenta/20",
        BackendType.Sycl => "bg-neon-cyan/10 text-neon-cyan border-neon-cyan/20",
        BackendType.Hip => "bg-red-500/10 text-red-400 border-red-500/20",
        BackendType.Metal => "bg-gray-400/10 text-gray-300 border-gray-400/20",
        BackendType.OpenCL => "bg-amber-500/10 text-amber-400 border-amber-500/20",
        BackendType.OpenVino => "bg-blue-500/10 text-blue-400 border-blue-500/20",
        _ => "bg-gray-700/30 text-text-secondary border-gray-600/20",
    };

    public static string BackendLabel(BackendType b) => b switch
    {
        BackendType.Hip => "HIP/ROCm",
        BackendType.OpenVino => "OpenVINO",
        BackendType.OpenCL => "OpenCL",
        _ => b.ToString(),
    };
}
