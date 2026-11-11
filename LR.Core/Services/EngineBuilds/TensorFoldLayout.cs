using System.Text.RegularExpressions;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// Where things are in a TensorFold install (github.com/ashhart/TensorFold). A release archive
/// unpacks to <c>bin/tensorfold-native</c> beside <c>lib/</c> (the grammar library structured output
/// and required tool calls load), so the install folder is the archive root and the executable sits
/// in its <c>bin</c>. A Windows build from source has <c>zig-out\native\bin\tensorfold-native.exe</c>;
/// pointing a server at <c>zig-out\native</c> or straight at its <c>bin</c> both work.
/// </summary>
public static partial class TensorFoldLayout
{
    public const string Repo = "ashhart/TensorFold";

    /// <summary>The executable's subfolder in a release install.</summary>
    public const string BinFolder = "bin";

    public static string ExecutableName(bool windows) => windows ? "tensorfold-native.exe" : "tensorfold-native";

    /// <summary>
    /// The executable in <paramref name="installFolder"/>: <c>bin/tensorfold-native</c>, or the folder's
    /// own <c>tensorfold-native</c> when it holds one. Without <paramref name="fileExists"/> (a remote
    /// machine, which can't be looked at from here) it's the release layout.
    /// </summary>
    public static string ExecutablePath(string installFolder, bool windows, Func<string, bool>? fileExists = null)
    {
        var name = ExecutableName(windows);
        var inBin = Combine(windows, installFolder, BinFolder, name);
        if (fileExists is null || fileExists(inBin))
            return inBin;
        var direct = Combine(windows, installFolder, name);
        return fileExists(direct) ? direct : inBin;
    }

    /// <summary>Whether <paramref name="installFolder"/> on this machine holds a TensorFold executable.</summary>
    public static bool IsInstall(string installFolder)
    {
        var windows = OperatingSystem.IsWindows();
        return File.Exists(ExecutablePath(installFolder, windows, File.Exists));
    }

    /// <summary>The version a release install records in its <c>VERSION</c> file, or null.</summary>
    public static string? ReadVersion(string installFolder)
    {
        try
        {
            var path = Path.Combine(installFolder, "VERSION");
            return File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: > 0 } v ? v : null : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Whether a preset's model is a Hugging Face repo id (<c>owner/name</c>, optionally <c>@revision</c>)
    /// rather than a checkpoint folder: TensorFold serves either, but only a repo id can be pulled.
    /// </summary>
    public static bool IsRepoId(string model) => RepoIdPattern().IsMatch(model.Trim());

    /// <summary>The repo id without its <c>@revision</c>: what <c>serve</c> takes, where <c>pull</c> takes the pinned form.</summary>
    public static string RepoWithoutRevision(string repoId)
    {
        var trimmed = repoId.Trim();
        var at = trimmed.IndexOf('@');
        return at < 0 ? trimmed : trimmed[..at];
    }

    /// <summary>The repo's folder in the Hugging Face hub cache (<c>models--owner--name</c>).</summary>
    public static string HubCacheFolderName(string repoId) => "models--" + RepoWithoutRevision(repoId).Replace("/", "--");

    private static string Combine(bool windows, params string[] parts)
    {
        var sep = windows ? '\\' : '/';
        var result = parts[0].TrimEnd('/', '\\');
        foreach (var part in parts.Skip(1))
            result += sep + part;
        return result;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][\w.\-]*/[\w.\-]+(@[\w.\-]+)?$")]
    private static partial Regex RepoIdPattern();
}
