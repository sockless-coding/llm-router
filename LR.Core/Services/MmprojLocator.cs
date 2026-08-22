namespace LR.Core.Services;

/// <summary>
/// Finds a multimodal projector (mmproj) .gguf file sitting next to a model file. llama.cpp
/// vision models are converted to GGUF as two separate files — the text backbone and the
/// projector — that repos (e.g. unsloth's) ship side by side in the same folder, so a sibling
/// filename match is enough to wire vision support up without the user hunting for it.
/// </summary>
public static class MmprojLocator
{
    /// <summary>
    /// Preferred projector precision, checked in order, when a folder has more than one mmproj
    /// candidate (e.g. both an F16 and a BF16 build) — F16 is more broadly supported by llama.cpp
    /// backends than BF16, so it's preferred when both are present.
    /// </summary>
    private static readonly string[] PreferredKeywords = { "f16", "bf16", "fp16", "fp32" };

    /// <summary>
    /// Looks in <paramref name="modelPath"/>'s own directory (non-recursive) for a .gguf file
    /// whose name contains "mmproj", excluding the model file itself. Returns null if the
    /// directory doesn't exist or no candidate is found.
    /// </summary>
    public static string? FindSiblingMmproj(string modelPath)
    {
        var dir = Path.GetDirectoryName(modelPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return null;

        var modelFileName = Path.GetFileName(modelPath);

        var candidates = Directory.EnumerateFiles(dir, "*.gguf")
            .Where(f => !string.Equals(Path.GetFileName(f), modelFileName, StringComparison.OrdinalIgnoreCase))
            .Where(f => Path.GetFileName(f).Contains("mmproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(PreferenceRank)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates.FirstOrDefault();
    }

    private static int PreferenceRank(string filePath)
    {
        var name = Path.GetFileName(filePath);
        var rank = Array.FindIndex(PreferredKeywords, k => ContainsKeyword(name, k));
        return rank >= 0 ? rank : PreferredKeywords.Length;
    }

    /// <summary>
    /// Substring match that requires the keyword not be glued onto a preceding letter, so "f16"
    /// matches "mmproj-F16.gguf" but not the "f16" inside "mmproj-BF16.gguf".
    /// </summary>
    private static bool ContainsKeyword(string name, string keyword)
    {
        var searchFrom = 0;
        while (true)
        {
            var idx = name.IndexOf(keyword, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return false;
            if (idx == 0 || !char.IsLetter(name[idx - 1]))
                return true;
            searchFrom = idx + 1;
        }
    }
}
