using System.Text.Json;

using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// What downloading a Hugging Face checkpoint (<see cref="ModelFormat.Checkpoint"/>) fetches, and the
/// manifest that lets a download resume and an update fetch only what changed.
/// </summary>
public static class CheckpointDownload
{
    /// <summary>Written in the checkpoint folder: each downloaded file's git blob id.</summary>
    public const string ManifestFileName = ".llm-router-checkpoint.json";

    /// <summary>Folders that hold other formats of the same weights (or the original, unconverted ones), or training logs.</summary>
    private static readonly string[] SkippedFolders = ["original/", "onnx/", "gguf/", "openvino/", "coreml/", "metal/", "mlx/", "tflite/", "runs/", "checkpoint-"];

    /// <summary>Weights in formats checkpoint engines don't load, and media the model card shows.</summary>
    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gguf", ".bin", ".pt", ".pth", ".ckpt", ".onnx", ".onnx_data", ".h5", ".msgpack", ".ot", ".tflite", ".mlmodel", ".pb", ".npz",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".mp4", ".pdf", ".zip", ".tar", ".gz",
    };

    /// <summary>Whether a repo file belongs in the checkpoint folder.</summary>
    public static bool Include(string repoPath)
    {
        if (repoPath.StartsWith('.') || repoPath.Contains("/.", StringComparison.Ordinal))
            return false;
        if (SkippedFolders.Any(f => repoPath.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            return false;
        return !SkippedExtensions.Contains(Path.GetExtension(repoPath));
    }

    /// <summary>The files to download from a repo, or null when it isn't a checkpoint (no config.json and safetensors weights).</summary>
    public static IReadOnlyList<HfRepoFile>? Select(IEnumerable<HfRepoFile> siblings)
    {
        var files = siblings.Where(f => Include(f.Filename)).OrderBy(f => f.Filename, StringComparer.Ordinal).ToList();
        var isCheckpoint = files.Any(f => f.Filename == "config.json")
            && files.Any(f => !f.Filename.Contains('/') && f.Filename.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase));
        return isCheckpoint ? files : null;
    }

    /// <summary>The folder a repo's checkpoint goes in under the library root.</summary>
    public static string FolderFor(string root, string repoId) =>
        Path.GetFullPath(Path.Combine(root, "checkpoints", repoId.Replace('/', '_')));

    /// <summary>The blob ids of the files already downloaded into <paramref name="folder"/>.</summary>
    public static Dictionary<string, string> ReadManifest(string folder)
    {
        try
        {
            var path = Path.Combine(folder, ManifestFileName);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public static void WriteManifest(string folder, Dictionary<string, string> manifest) =>
        File.WriteAllText(Path.Combine(folder, ManifestFileName), JsonSerializer.Serialize(manifest));

    /// <summary>
    /// Whether <paramref name="file"/> is already in <paramref name="folder"/> as the repo has it now: the
    /// manifest names its blob and the file on disk has the expected size.
    /// </summary>
    public static bool IsCurrent(string folder, HfRepoFile file, IReadOnlyDictionary<string, string> manifest)
    {
        if (file.BlobId is null || !manifest.TryGetValue(file.Filename, out var blob) || blob != file.BlobId)
            return false;
        var local = new FileInfo(Path.Combine(folder, file.Filename));
        return local.Exists && (file.SizeBytes is null || local.Length == file.SizeBytes);
    }
}
