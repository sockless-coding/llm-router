using System.Text.RegularExpressions;

namespace LR.Core.Services;

/// <summary>
/// GGUF models split into shards, named <c>&lt;name&gt;-00001-of-0000N.gguf</c> … <c>-0000N-of-0000N.gguf</c>
/// (llama.cpp's <c>gguf-split</c> convention, which Hugging Face repos use for large models). The model
/// library keeps a split model as one entry whose path is the first shard; llama.cpp and Strata both
/// find the other shards next to it.
/// </summary>
public static partial class SplitGguf
{
    /// <summary>
    /// If <paramref name="fileName"/> (a name or a path) is shard <c>i</c> of a split model, returns the
    /// shard number and the shard count; otherwise null.
    /// </summary>
    public static (int Index, int Count)? Parse(string fileName)
    {
        var match = ShardRegex().Match(fileName);
        return match.Success
            ? (int.Parse(match.Groups["i"].Value), int.Parse(match.Groups["n"].Value))
            : null;
    }

    /// <summary>True if <paramref name="fileName"/> is the first shard of a split model.</summary>
    public static bool IsFirstShard(string fileName) => Parse(fileName) is { Index: 1, Count: > 1 };

    /// <summary>
    /// Every shard of the split model whose first shard is <paramref name="firstShard"/> (a name or a
    /// path — the directory part is kept), in order; just <paramref name="firstShard"/> for a single file.
    /// </summary>
    public static IReadOnlyList<string> AllShards(string firstShard)
    {
        if (Parse(firstShard) is not { Index: 1, Count: > 1 } split)
            return [firstShard];

        var match = ShardRegex().Match(firstShard);
        var digits = match.Groups["i"].Value.Length;
        var prefix = firstShard[..match.Groups["i"].Index];
        var suffix = firstShard[(match.Groups["i"].Index + match.Groups["i"].Length)..];
        return Enumerable.Range(1, split.Count)
            .Select(i => prefix + i.ToString().PadLeft(digits, '0') + suffix)
            .ToList();
    }

    [GeneratedRegex(@"-(?<i>\d{5})-of-(?<n>\d{5})\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex ShardRegex();
}
