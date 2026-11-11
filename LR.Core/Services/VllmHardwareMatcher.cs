using System.Text.RegularExpressions;

using LR.Core.Interfaces;
using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Which recipes.vllm.ai hardware profiles look like the GPUs this machine has, so the recipe views can
/// start filtered to them. A guess from device names ("Intel(R) Arc(TM) Pro B70 Graphics" → Arc Pro B70),
/// never a check that a recipe will run.
/// </summary>
public static partial class VllmHardwareMatcher
{
    public static IReadOnlyList<VllmHardwareProfile> Match(IEnumerable<ComputeDevice> devices, IEnumerable<VllmHardwareProfile> profiles)
    {
        var names = devices.Where(d => d.Kind == ComputeDeviceKind.Gpu).Select(d => Normalize(d.Name)).Where(n => n.Length > 0).ToList();
        if (names.Count == 0)
            return [];
        return profiles
            .Where(p => Normalize(p.DisplayName) is { Length: >= 3 } profile && names.Any(n => n.Contains(profile, StringComparison.Ordinal)))
            // The closest profile first: one sized for as many GPUs as there are, then the fewest GPUs.
            .OrderBy(p => Math.Abs(p.GpuCount - names.Count))
            .ThenBy(p => p.GpuCount)
            .ToList();
    }

    /// <summary>Lowercase letters and digits only, without trademark marks and vendor words.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        var text = TrademarkRegex().Replace(name, " ");
        text = VendorRegex().Replace(text, " ");
        return new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    [GeneratedRegex(@"\((R|TM|C)\)|[®™©]", RegexOptions.IgnoreCase)]
    private static partial Regex TrademarkRegex();

    [GeneratedRegex(@"\b(NVIDIA|GeForce|Intel|AMD|Radeon|Graphics|Apple|Google|Huawei)\b", RegexOptions.IgnoreCase)]
    private static partial Regex VendorRegex();
}
