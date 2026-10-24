using System.Text.RegularExpressions;

using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// What the prebuilt-release pipeline (<see cref="ResolveReleaseAssetStep"/> → <see cref="DownloadArchiveStep"/>
/// → <see cref="ExtractArchiveStep"/> → <see cref="FinalizeBuildStep"/>) needs to know about an engine
/// that publishes ready-made server binaries on GitHub releases: where they live, which archive fits
/// a backend on this host, and which executable proves the install worked. One per such engine
/// (llama.cpp, stable-diffusion.cpp); Strata installs differently and has no profile.
/// </summary>
public abstract class EngineReleaseProfile
{
    public abstract ServerEngine Engine { get; }

    /// <summary>Engine name used in build names and progress messages.</summary>
    public abstract string DisplayName { get; }

    /// <summary>GitHub repo ("owner/name") the releases come from.</summary>
    public abstract string Repo { get; }

    /// <summary>The server executable for this OS that a finished install must contain.</summary>
    public abstract string ServerExecutableName { get; }

    /// <summary>Prefix for install folder names, so builds of different engines never collide.</summary>
    public abstract string FolderPrefix { get; }

    /// <summary>The backends worth offering on the releases page, in display order.</summary>
    public abstract IReadOnlyList<BackendType> CandidateBackends { get; }

    /// <summary>Whether a release tag is an installable build (skips marker/nightly tags).</summary>
    public abstract bool IsInstallableRelease(string tagName);

    /// <summary>The version recorded on the build row for a release tag (or a source build's <c>git describe</c>).</summary>
    public virtual string VersionTagOf(string tagName) => tagName;

    /// <summary>Regex the primary binary archive name must match, or null for a combo the engine doesn't publish.</summary>
    public abstract Regex? PrimaryAssetPattern(BackendType backend, string os, string arch);

    /// <summary>Any extra archives the primary one needs (e.g. the CUDA runtime on Windows).</summary>
    protected virtual IEnumerable<GitHubReleaseAsset> CompanionAssets(
        IReadOnlyList<GitHubReleaseAsset> assets, GitHubReleaseAsset primary, BackendType backend, string os, string arch) => [];

    /// <summary>Orders several matching primary archives — the first is taken.</summary>
    protected virtual IEnumerable<GitHubReleaseAsset> RankCandidates(IEnumerable<GitHubReleaseAsset> candidates) => candidates;

    /// <summary>Whether a target has any prebuilt archive in the given release.</summary>
    public bool IsAvailable(IReadOnlyList<GitHubReleaseAsset> assets, BackendType backend, string os, string arch)
    {
        var pattern = PrimaryAssetPattern(backend, os, arch);
        return pattern is not null && assets.Any(x => pattern.IsMatch(x.Name));
    }

    /// <summary>
    /// Resolves the archive(s) to download for a target: the primary binary archive plus its
    /// companions. Throws if nothing matches.
    /// </summary>
    public virtual IReadOnlyList<GitHubReleaseAsset> Resolve(IReadOnlyList<GitHubReleaseAsset> assets, BackendType backend, string os, string arch)
    {
        var pattern = PrimaryAssetPattern(backend, os, arch)
            ?? throw new InvalidOperationException($"{DisplayName} does not publish a prebuilt {backend} archive for {os}-{arch}.");

        var primary = RankCandidates(assets.Where(x => pattern.IsMatch(x.Name))).FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No release asset matched {pattern} (available: {string.Join(", ", assets.Select(x => x.Name))}).");

        return [primary, .. CompanionAssets(assets, primary, backend, os, arch)];
    }

    /// <summary>The profile for <paramref name="engine"/>, or null if it doesn't install from prebuilt releases.</summary>
    public static EngineReleaseProfile? For(ServerEngine engine) => engine switch
    {
        ServerEngine.LlamaCpp => LlamaCppReleaseProfile.Instance,
        ServerEngine.StableDiffusionCpp => SdCppReleaseProfile.Instance,
        _ => null,
    };

    protected static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase);
}

/// <summary>llama.cpp's <c>b####</c> releases — asset matching lives in <see cref="ReleaseAssetResolver"/>.</summary>
public sealed class LlamaCppReleaseProfile : EngineReleaseProfile
{
    public static readonly LlamaCppReleaseProfile Instance = new();

    public override ServerEngine Engine => ServerEngine.LlamaCpp;
    public override string DisplayName => "llama.cpp";
    public override string Repo => EngineBuildManager.LlamaCppRepo;
    public override string ServerExecutableName => OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";
    public override string FolderPrefix => "";

    public override IReadOnlyList<BackendType> CandidateBackends { get; } =
    [
        BackendType.Cpu, BackendType.Cuda, BackendType.Vulkan, BackendType.Sycl,
        BackendType.Hip, BackendType.OpenVino, BackendType.OpenCL, BackendType.Metal,
    ];

    public override bool IsInstallableRelease(string tagName) => Regex.IsMatch(tagName, @"^b\d{3,}$", RegexOptions.IgnoreCase);

    public override string VersionTagOf(string tagName) => ReleaseAssetResolver.ExtractBuildTag(tagName) ?? tagName;

    public override Regex? PrimaryAssetPattern(BackendType backend, string os, string arch) =>
        ReleaseAssetResolver.PrimaryAssetPattern(backend, os, arch);

    // Delegates wholesale so llama.cpp keeps its toolkit-version ranking and cudart pairing.
    public override IReadOnlyList<GitHubReleaseAsset> Resolve(IReadOnlyList<GitHubReleaseAsset> assets, BackendType backend, string os, string arch) =>
        ReleaseAssetResolver.Resolve(assets, backend, os, arch);
}

/// <summary>
/// stable-diffusion.cpp's <c>master-&lt;n&gt;-&lt;sha&gt;</c> releases. Asset naming (as of master-999):
/// <list type="bullet">
/// <item>Windows: <c>sd-master-&lt;sha&gt;-bin-win-cpu-x64.zip</c> (older: <c>-win-avx2-x64</c>),
///   <c>-win-cuda12-x64.zip</c>, <c>-win-vulkan-x64.zip</c>, <c>-win-rocm-&lt;ver&gt;-x64.zip</c></item>
/// <item>Linux: <c>sd-master-&lt;sha&gt;-bin-Linux-Ubuntu-24.04-x86_64.zip</c> (CPU), <c>…-x86_64-vulkan.zip</c>,
///   <c>…-x86_64-rocm-&lt;ver&gt;.zip</c></item>
/// <item>macOS: <c>sd-master-&lt;sha&gt;-bin-Darwin-macOS-&lt;ver&gt;-arm64.zip</c> (CPU + Metal)</item>
/// </list>
/// Windows CUDA additionally needs <c>cudart-sd-bin-win-cu&lt;major&gt;-x64.zip</c>.
/// </summary>
public sealed class SdCppReleaseProfile : EngineReleaseProfile
{
    public static readonly SdCppReleaseProfile Instance = new();

    public const string RepoName = "leejet/stable-diffusion.cpp";

    /// <summary>The <c>sd-server</c> executable name for this OS.</summary>
    public static string ServerExecutable => OperatingSystem.IsWindows() ? "sd-server.exe" : "sd-server";

    public override ServerEngine Engine => ServerEngine.StableDiffusionCpp;
    public override string DisplayName => "stable-diffusion.cpp";
    public override string Repo => RepoName;
    public override string ServerExecutableName => ServerExecutable;
    public override string FolderPrefix => "sd-";

    public override IReadOnlyList<BackendType> CandidateBackends { get; } =
        [BackendType.Cpu, BackendType.Cuda, BackendType.Vulkan, BackendType.Hip, BackendType.Metal];

    public override bool IsInstallableRelease(string tagName) => Regex.IsMatch(tagName, @"^master-\d+-[0-9a-f]{7,}$", RegexOptions.IgnoreCase);

    public override Regex? PrimaryAssetPattern(BackendType backend, string os, string arch) => (os, arch, backend) switch
    {
        ("win", "x64", BackendType.Cpu) => Rx(@"^sd-.*-bin-win-(cpu|avx2)-x64\.zip$"),
        ("win", "x64", BackendType.Cuda) => Rx(@"^sd-.*-bin-win-cuda\d+-x64\.zip$"),
        ("win", "x64", BackendType.Vulkan) => Rx(@"^sd-.*-bin-win-vulkan-x64\.zip$"),
        ("win", "x64", BackendType.Hip) => Rx(@"^sd-.*-bin-win-rocm-[\d.]+-x64\.zip$"),

        ("ubuntu", "x64", BackendType.Cpu) => Rx(@"^sd-.*-bin-Linux-Ubuntu-[\d.]+-x86_64\.zip$"),
        ("ubuntu", "x64", BackendType.Vulkan) => Rx(@"^sd-.*-bin-Linux-Ubuntu-[\d.]+-x86_64-vulkan\.zip$"),
        ("ubuntu", "x64", BackendType.Hip) => Rx(@"^sd-.*-bin-Linux-Ubuntu-[\d.]+-x86_64-rocm-[\d.]+\.zip$"),

        // macOS archives are CPU + Metal in one.
        ("macos", "arm64", BackendType.Cpu or BackendType.Metal) => Rx(@"^sd-.*-bin-Darwin-macOS-[\d.]+-arm64\.zip$"),

        _ => null,
    };

    // Prefer the plain CPU build over the older AVX2-specific one; otherwise newest toolkit first.
    protected override IEnumerable<GitHubReleaseAsset> RankCandidates(IEnumerable<GitHubReleaseAsset> candidates) =>
        candidates.OrderByDescending(a => a.Name.Contains("-cpu-", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(a => ToolkitMajor(a.Name));

    protected override IEnumerable<GitHubReleaseAsset> CompanionAssets(
        IReadOnlyList<GitHubReleaseAsset> assets, GitHubReleaseAsset primary, BackendType backend, string os, string arch)
    {
        if (backend != BackendType.Cuda || os != "win")
            yield break;

        var major = ToolkitMajor(primary.Name);
        var cudart = assets.FirstOrDefault(a => Regex.IsMatch(a.Name, $@"^cudart-sd-bin-win-cu{major}-x64\.zip$", RegexOptions.IgnoreCase))
            ?? assets.FirstOrDefault(a => Regex.IsMatch(a.Name, @"^cudart-sd-bin-win-cu\d+-x64\.zip$", RegexOptions.IgnoreCase));
        if (cudart is not null)
            yield return cudart;
    }

    private static int ToolkitMajor(string name)
    {
        var m = Regex.Match(name, @"-(?:cuda|rocm-)(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }
}
