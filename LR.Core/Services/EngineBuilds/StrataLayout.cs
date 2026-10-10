using System.Text.Json;
using System.Text.RegularExpressions;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// What a Strata checkout (github.com/Niko1221/Strata) looks like on disk, and how to read its
/// versions — shared by the Strata provider (launching it) and the engine-build registry
/// (tracking and updating it). Everything here is read-only and best-effort.
/// </summary>
public static partial class StrataLayout
{
    public const string Repo = "Niko1221/Strata";
    public const string RepoUrl = "https://github.com/Niko1221/Strata.git";

    /// <summary>The branch Strata's own updater pulls.</summary>
    public const string Branch = "main";

    /// <summary>The frontend script, relative to the checkout.</summary>
    public static readonly string ServerScriptRelativePath = Path.Combine("serve", "server.py");

    /// <summary>Strata's setup/update script, relative to the checkout.</summary>
    public const string SetupScript = "setup.py";

    /// <summary>The Python interpreter Strata's setup creates, relative to the checkout.</summary>
    public static string PythonRelativePath => OperatingSystem.IsWindows()
        ? Path.Combine(".venv", "Scripts", "python.exe")
        : Path.Combine(".venv", "bin", "python");

    /// <summary>The script a user runs for first-time setup on this OS.</summary>
    public static string FirstTimeSetupCommand => OperatingSystem.IsWindows() ? "START-HERE.bat" : "./setup.sh";

    public static string PythonPath(string checkout) => Path.Combine(checkout, PythonRelativePath);

    /// <summary>True if <paramref name="folder"/> contains Strata's sources.</summary>
    public static bool IsCheckout(string folder) =>
        File.Exists(Path.Combine(folder, ServerScriptRelativePath)) && File.Exists(Path.Combine(folder, SetupScript));

    /// <summary>
    /// True if the checkout can run: its <c>.venv</c> exists and an engine is installed. (On Linux there
    /// are no ready-made engines; Strata's setup compiles one the first time a model is prepared.)
    /// </summary>
    public static bool IsSetUp(string folder) =>
        IsCheckout(folder) && File.Exists(PythonPath(folder)) && (InstalledEngine(folder) is not null || !OperatingSystem.IsWindows());

    /// <summary>The engine executable's name on this OS.</summary>
    public static string EngineExecutable => OperatingSystem.IsWindows() ? "strata.exe" : "strata";

    /// <summary>
    /// The engine build a release asset holds, by Strata's asset names: <c>strata-&lt;os&gt;-x64.zip</c> (CUDA),
    /// <c>…-cuda12.zip</c> (the CUDA 12 build), <c>…-hip.zip</c> (AMD); null for anything else.
    /// </summary>
    public static StrataEngineVariant? VariantOfAsset(string assetName)
    {
        var os = OperatingSystem.IsWindows() ? "windows" : "linux";
        if (string.Equals(assetName, $"strata-{os}-x64.zip", StringComparison.OrdinalIgnoreCase)) return StrataEngineVariant.Cuda;
        if (string.Equals(assetName, $"strata-{os}-x64-cuda12.zip", StringComparison.OrdinalIgnoreCase)) return StrataEngineVariant.Cuda12;
        if (string.Equals(assetName, $"strata-{os}-x64-hip.zip", StringComparison.OrdinalIgnoreCase)) return StrataEngineVariant.Hip;
        return null;
    }

    /// <summary>The checkout folder an engine variant is installed in, where Strata's setup looks for it.</summary>
    public static string EngineFolder(StrataEngineVariant variant) => variant == StrataEngineVariant.Cuda12 ? "engine-cuda12" : "engine";

    /// <summary>The engine installed in <paramref name="checkout"/>, if any (<c>engine/</c> first, as Strata's setup prefers it).</summary>
    public static StrataEngineVariant? InstalledEngine(string checkout)
    {
        if (File.Exists(Path.Combine(checkout, "engine", EngineExecutable)))
            return string.Equals(ReadBuildInfo(Path.Combine(checkout, "engine"), "backend"), "hip", StringComparison.OrdinalIgnoreCase)
                ? StrataEngineVariant.Hip
                : StrataEngineVariant.Cuda;
        if (File.Exists(Path.Combine(checkout, "engine-cuda12", EngineExecutable)))
            return StrataEngineVariant.Cuda12;
        return null;
    }

    /// <summary>The <c>setup.py</c> flags that make it use (rather than replace) an installed engine variant.</summary>
    public static IReadOnlyList<string> SetupFlags(StrataEngineVariant? variant) => variant switch
    {
        StrataEngineVariant.Cuda12 => ["--cuda", "12"],
        StrataEngineVariant.Hip => ["--backend", "hip"],
        _ => [],
    };

    public static string Describe(StrataEngineVariant variant) => variant switch
    {
        StrataEngineVariant.Cuda12 => "CUDA 12",
        StrataEngineVariant.Hip => "AMD (HIP)",
        _ => "CUDA",
    };

    /// <summary>
    /// True if <paramref name="folder"/> holds Strata model data (its <c>models</c>, <c>packs</c> or
    /// <c>mtp</c> folder, non-empty) — tens of GB that must never be deleted along with a checkout.
    /// </summary>
    public static bool HoldsModelData(string folder) =>
        new[] { "models", "packs", "mtp" }.Any(d =>
        {
            try { return Directory.Exists(Path.Combine(folder, d)) && Directory.EnumerateFileSystemEntries(Path.Combine(folder, d)).Any(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        });

    /// <summary>True for Strata's release tags (<c>v0.1.41</c>, <c>v0.1.40.4</c>).</summary>
    public static bool IsReleaseTag(string tag) => ReleaseTagRegex().IsMatch(tag);

    public static bool IsGitClone(string folder) => Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git"));

    /// <summary>The version the sources build — <c>project(strata VERSION x.y.z)</c> in CMakeLists.txt.</summary>
    public static string? ReadSourceVersion(string folder)
    {
        try
        {
            var match = CMakeVersionRegex().Match(File.ReadAllText(Path.Combine(folder, "CMakeLists.txt")));
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The installed engine binary's version from <c>engine/BUILD.json</c>. Can lag the source
    /// version: Strata keeps the previous engine when compiling a newer one fails.
    /// </summary>
    public static string? ReadEngineVersion(string folder) =>
        InstalledEngine(folder) is { } variant ? ReadBuildInfo(Path.Combine(folder, EngineFolder(variant)), "version") : null;

    /// <summary>A string field of an engine folder's <c>BUILD.json</c>, or null.</summary>
    private static string? ReadBuildInfo(string engineFolder, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(engineFolder, "BUILD.json")));
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The model run configs (<c>strata-&lt;model&gt;.json</c>) Strata's setup has written in the
    /// checkout — what a preset for a Strata server points at. Other <c>strata-*.json</c> files
    /// (e.g. shared chat settings) are skipped: a run config names the engine <c>exe</c> and its <c>args</c>.
    /// </summary>
    public static IReadOnlyList<StrataRunConfig> FindRunConfigs(string folder)
    {
        var configs = new List<StrataRunConfig>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "strata-*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("exe", out _) || !root.TryGetProperty("args", out _))
                        continue;

                    string model = root.TryGetProperty("model_name", out var name) && name.ValueKind == JsonValueKind.String
                        ? name.GetString()!
                        : Path.GetFileNameWithoutExtension(path)["strata-".Length..];
                    string? backend = root.TryGetProperty("backend", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
                    configs.Add(new StrataRunConfig(Path.GetFileName(path), model, backend));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Not a readable run config — skip it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Folder unreadable — nothing found.
        }
        return configs;
    }

    [GeneratedRegex(@"project\(\s*strata\s+VERSION\s+([\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CMakeVersionRegex();

    [GeneratedRegex(@"^v\d+(\.\d+){1,3}$")]
    private static partial Regex ReleaseTagRegex();
}

/// <summary>Strata's ready-made engine builds (one per release asset).</summary>
public enum StrataEngineVariant
{
    /// <summary>The default NVIDIA build (CUDA 13), in <c>engine/</c>.</summary>
    Cuda,

    /// <summary>The experimental CUDA 12 build for older drivers, in <c>engine-cuda12/</c>.</summary>
    Cuda12,

    /// <summary>The AMD build (HIP/ROCm), in <c>engine/</c>.</summary>
    Hip,
}

/// <param name="FileName">The run config's file name, relative to the checkout (what a preset's model path can be).</param>
/// <param name="ModelName">The model it runs.</param>
/// <param name="Backend">Strata's GPU backend for it (<c>"hip"</c> for AMD), or null for the default (CUDA).</param>
public sealed record StrataRunConfig(string FileName, string ModelName, string? Backend);
