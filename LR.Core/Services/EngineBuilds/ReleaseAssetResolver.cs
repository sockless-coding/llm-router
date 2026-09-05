using System.Text.RegularExpressions;

using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// Picks the prebuilt archive(s) matching a (<see cref="BackendType"/>, OS, arch) target from a
/// <c>ggml-org/llama.cpp</c> GitHub release. Current asset naming (as of the b10800 releases):
/// <list type="bullet">
/// <item>Windows: <c>llama-b####-bin-win-cpu-x64.zip</c>, <c>-win-cuda-12.4-x64.zip</c> (versioned,
///   several), <c>-win-sycl-x64.zip</c>, <c>-win-vulkan-x64.zip</c></item>
/// <item>Ubuntu: <c>llama-b####-bin-ubuntu-x64.tar.gz</c> (CPU), <c>-ubuntu-vulkan-x64.tar.gz</c>,
///   <c>-ubuntu-sycl-fp32-x64.tar.gz</c> / <c>-sycl-fp16-x64.tar.gz</c></item>
/// <item>macOS: <c>llama-b####-bin-macos-arm64.tar.gz</c> / <c>-macos-x64.tar.gz</c> (CPU/Metal)</item>
/// </list>
/// Windows CUDA additionally needs the matching <c>cudart-llama-bin-win-cuda-&lt;ver&gt;-&lt;arch&gt;.zip</c>
/// runtime archive.
/// </summary>
public static class ReleaseAssetResolver
{
    public static (string Os, string Arch) DetectHost()
    {
        string os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "macos"
            : "ubuntu";
        string arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        return (os, arch);
    }

    /// <summary>Regex the primary binary archive name must match, or null for combos llama.cpp doesn't publish.</summary>
    public static Regex? PrimaryAssetPattern(BackendType backend, string os, string arch)
    {
        string a = Regex.Escape(arch);
        return (os, backend) switch
        {
            ("win", BackendType.Cpu) => Rx($@"^llama-.*-bin-win-cpu-{a}\.zip$"),
            ("win", BackendType.Cuda) => Rx($@"^llama-.*-bin-win-cuda-[\d.]+-{a}\.zip$"),
            ("win", BackendType.Vulkan) => Rx($@"^llama-.*-bin-win-vulkan-{a}\.zip$"),
            ("win", BackendType.Sycl) => Rx($@"^llama-.*-bin-win-sycl-{a}\.zip$"),

            ("ubuntu", BackendType.Cpu) => Rx($@"^llama-.*-bin-ubuntu-{a}\.tar\.gz$"),
            ("ubuntu", BackendType.Vulkan) => Rx($@"^llama-.*-bin-ubuntu-vulkan-{a}\.tar\.gz$"),
            ("ubuntu", BackendType.Sycl) => Rx($@"^llama-.*-bin-ubuntu-sycl-fp32-{a}\.tar\.gz$"),

            ("macos", BackendType.Cpu) => Rx($@"^llama-.*-bin-macos-{a}\.tar\.gz$"),

            _ => null,
        };
    }

    /// <summary>
    /// Resolves the archive(s) to download for a target. Returns the primary binary archive plus,
    /// for Windows CUDA, the matching CUDA-runtime archive. Throws if nothing matches.
    /// </summary>
    public static IReadOnlyList<GitHubReleaseAsset> Resolve(
        IReadOnlyList<GitHubReleaseAsset> assets, BackendType backend, string os, string arch)
    {
        var pattern = PrimaryAssetPattern(backend, os, arch)
            ?? throw new InvalidOperationException(
                $"llama.cpp does not publish a prebuilt {backend} archive for {os}-{arch}. Compile from source instead.");

        var candidates = assets.Where(x => pattern.IsMatch(x.Name)).ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"No release asset matched {pattern} (available: {string.Join(", ", assets.Select(x => x.Name))}).");

        // Windows CUDA ships one archive per CUDA toolkit version — take the highest.
        var primary = candidates
            .OrderByDescending(x => ExtractVersion(x.Name))
            .First();

        var result = new List<GitHubReleaseAsset> { primary };

        if (backend == BackendType.Cuda && os == "win")
        {
            var cudaVer = ExtractCudaVersion(primary.Name);
            var cudartPattern = cudaVer is not null
                ? Rx($@"^cudart-llama-bin-win-cuda-{Regex.Escape(cudaVer)}-{Regex.Escape(arch)}\.zip$")
                : Rx(@"^cudart-llama-bin-win-cuda-[\d.]+-" + Regex.Escape(arch) + @"\.zip$");
            var cudart = assets.FirstOrDefault(x => cudartPattern.IsMatch(x.Name));
            if (cudart is not null)
                result.Add(cudart);
        }

        return result;
    }

    /// <summary>Whether a target has any prebuilt archive in the given release.</summary>
    public static bool IsAvailable(IReadOnlyList<GitHubReleaseAsset> assets, BackendType backend, string os, string arch)
    {
        var pattern = PrimaryAssetPattern(backend, os, arch);
        return pattern is not null && assets.Any(x => pattern.IsMatch(x.Name));
    }

    /// <summary>Pulls the <c>b####</c> build number out of a release tag or asset name.</summary>
    public static string? ExtractBuildTag(string text)
    {
        var m = Regex.Match(text, @"\bb\d{3,}\b", RegexOptions.IgnoreCase);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase);

    private static Version ExtractVersion(string name)
    {
        var m = Regex.Match(name, @"cuda-(\d+)\.(\d+)");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : new Version(0, 0);
    }

    private static string? ExtractCudaVersion(string name)
    {
        var m = Regex.Match(name, @"cuda-(\d+\.\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }
}
