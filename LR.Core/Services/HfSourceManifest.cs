using System.Text.Json;
using System.Text.RegularExpressions;

using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Where the model files in a folder came from on Hugging Face (<see cref="FileName"/>). Written into a
/// repo's download folder (<c>{root}/{owner}_{name}/</c>) or a checkpoint folder whenever a download
/// finishes, so another router instance sharing the model root can link the same files to their repo,
/// and see the revision they're at after an update made elsewhere.
/// </summary>
public sealed class HfSourceManifest
{
    public const string FileName = ".llm-router-hf.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string RepoId { get; set; } = string.Empty;

    /// <summary>For a checkpoint folder: the revision the whole folder is at.</summary>
    public string? Revision { get; set; }

    /// <summary>Single-file downloads: the file's path in the repo (forward slashes) → the revision it was fetched at.</summary>
    public Dictionary<string, string?> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static HfSourceManifest? TryRead(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path)) return null;
            var manifest = JsonSerializer.Deserialize<HfSourceManifest>(File.ReadAllText(path), JsonOptions);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.RepoId)) return null;
            manifest.Files = new Dictionary<string, string?>(manifest.Files ?? new(), StringComparer.OrdinalIgnoreCase);
            return manifest;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Best effort: a read-only share just doesn't get a manifest.</summary>
    private bool TryWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Records that <paramref name="localPath"/> is <paramref name="repoFilename"/> of <paramref name="repoId"/>
    /// at <paramref name="revision"/>. The manifest goes in the folder the repo's files are laid out under:
    /// <paramref name="localPath"/> with <paramref name="repoFilename"/>'s folders taken off.
    /// </summary>
    public static bool RecordFile(string localPath, string repoId, string repoFilename, string? revision)
    {
        var folder = RepoFolderOf(localPath, repoFilename);
        if (folder is null) return false;
        var manifest = TryRead(folder) is { } existing && existing.RepoId.Equals(repoId, StringComparison.OrdinalIgnoreCase)
            ? existing
            : new HfSourceManifest { RepoId = repoId };
        manifest.Files[repoFilename.Replace('\\', '/')] = revision;
        return manifest.TryWrite(folder);
    }

    /// <summary>Records that checkpoint folder <paramref name="folder"/> is <paramref name="repoId"/> at <paramref name="revision"/>.</summary>
    public static bool RecordCheckpoint(string folder, string repoId, string? revision) =>
        new HfSourceManifest { RepoId = repoId, Revision = revision }.TryWrite(folder);

    /// <summary>The folder that holds <paramref name="repoFilename"/> at <paramref name="localPath"/>, or null when the path doesn't end that way.</summary>
    public static string? RepoFolderOf(string localPath, string repoFilename)
    {
        var depth = repoFilename.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        string? folder = localPath;
        for (int i = 0; i < depth && folder is not null; i++)
            folder = Path.GetDirectoryName(folder);
        return folder;
    }

    /// <summary>
    /// The Hugging Face source a library entry's manifest records, looking in its own folder (a checkpoint)
    /// or up to <paramref name="maxLevels"/> folders above its file, never above <paramref name="stopAt"/>.
    /// </summary>
    public static HfSourceMatch? Find(LocalModel model, string? stopAt, int maxLevels = 4)
    {
        if (model.Format == ModelFormat.Checkpoint)
        {
            var own = TryRead(model.FilePath);
            return own is null ? null : new HfSourceMatch(own.RepoId, null, own.Revision);
        }

        var stop = stopAt is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));
        var folder = Path.GetDirectoryName(model.FilePath);
        for (int level = 0; level < maxLevels && folder is not null; level++)
        {
            if (TryRead(folder) is { } manifest)
            {
                var relative = Path.GetRelativePath(folder, model.FilePath).Replace('\\', '/');
                if (manifest.Files.TryGetValue(relative, out var revision))
                    return new HfSourceMatch(manifest.RepoId, relative, revision);
            }
            if (stop is not null && string.Equals(Path.TrimEndingDirectorySeparator(folder), stop, StringComparison.OrdinalIgnoreCase))
                break;
            folder = Path.GetDirectoryName(folder);
        }
        return null;
    }
}

/// <summary>A model's Hugging Face source: the repo, the file's path in it (null for a whole checkpoint), and its revision when known.</summary>
public sealed record HfSourceMatch(string RepoId, string? Filename, string? Revision);

/// <summary>
/// Works out a model's Hugging Face repo from where its file sits, for files that have no
/// <see cref="HfSourceManifest"/> (downloaded before it existed, or by another tool).
/// </summary>
public static partial class HfPathInference
{
    /// <summary>
    /// A file in the Hugging Face hub cache (<c>models--owner--name/snapshots/{sha}/path</c>, as
    /// <c>huggingface-cli download</c> lays it out): the repo, revision and file are all in the path.
    /// For a snapshot folder itself (a checkpoint), the filename is null.
    /// </summary>
    public static HfSourceMatch? FromHubCache(string path)
    {
        var parts = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(p => p.Length > 0).ToArray();
        for (int i = 0; i + 2 < parts.Length; i++)
        {
            if (!parts[i].StartsWith("models--", StringComparison.Ordinal) || parts[i + 1] != "snapshots")
                continue;
            var repo = parts[i]["models--".Length..];
            var split = repo.IndexOf("--", StringComparison.Ordinal);
            if (split <= 0 || split + 2 >= repo.Length) return null;
            var repoId = $"{repo[..split]}/{repo[(split + 2)..]}";
            var revision = parts[i + 2];
            if (!CommitSha().IsMatch(revision)) return null;
            var filename = i + 3 < parts.Length ? string.Join('/', parts[(i + 3)..]) : null;
            return new HfSourceMatch(repoId, filename, revision);
        }
        return null;
    }

    /// <summary>
    /// The repos a file under the library root could be from, by the router's download layout:
    /// <c>{root}/{owner}_{name}/{file}</c>, or <c>{root}/checkpoints/{owner}_{name}</c> for a checkpoint.
    /// The <c>/</c> became <c>_</c>, and either part can hold underscores of its own, so every split is a
    /// candidate (checked against the Hub by the caller). Empty when the path isn't laid out that way.
    /// </summary>
    public static IReadOnlyList<HfSourceMatch> FromRouterLayout(string root, string path, bool checkpoint)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return Array.Empty<HfSourceMatch>();
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        string folder;
        string? filename;
        if (checkpoint)
        {
            if (parts.Length != 2 || !parts[0].Equals("checkpoints", StringComparison.OrdinalIgnoreCase)) return Array.Empty<HfSourceMatch>();
            folder = parts[1];
            filename = null;
        }
        else
        {
            if (parts.Length < 2) return Array.Empty<HfSourceMatch>();
            folder = parts[0];
            filename = string.Join('/', parts[1..]);
        }

        var candidates = new List<HfSourceMatch>();
        for (int i = folder.IndexOf('_'); i > 0 && i < folder.Length - 1; i = folder.IndexOf('_', i + 1))
            candidates.Add(new HfSourceMatch($"{folder[..i]}/{folder[(i + 1)..]}", filename, null));
        return candidates;
    }

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitSha();
}
