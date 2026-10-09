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
    /// True if Strata's setup has run there: its <c>.venv</c> exists and at least one model is set up
    /// (a run config) — before that there's nothing a server could run.
    /// </summary>
    public static bool IsSetUp(string folder) =>
        IsCheckout(folder) && File.Exists(PythonPath(folder)) && FindRunConfigs(folder).Count > 0;

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
    public static string? ReadEngineVersion(string folder)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "engine", "BUILD.json")));
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
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

/// <param name="FileName">The run config's file name, relative to the checkout (what a preset's model path can be).</param>
/// <param name="ModelName">The model it runs.</param>
/// <param name="Backend">Strata's GPU backend for it (<c>"hip"</c> for AMD), or null for the default (CUDA).</param>
public sealed record StrataRunConfig(string FileName, string ModelName, string? Backend);
