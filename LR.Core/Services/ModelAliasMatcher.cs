using System.Text.RegularExpressions;

using LR.Core.Models;

namespace LR.Core.Services;

/// <summary>
/// Resolves a client-supplied model name to a preset: exact preset name first (case-sensitive,
/// as before aliases existed), then an exact alias, then a wildcard alias — each tier
/// case-insensitive, first preset wins within a tier. Tiering keeps a broad pattern like
/// <c>gpt-*</c> on one preset from shadowing a specific <c>gpt-4o</c> alias on another.
/// </summary>
public static class ModelAliasMatcher
{
    private static readonly char[] Separators = [',', '\n', '\r', ';'];

    public static IEnumerable<string> ParseAliases(string? aliases) =>
        string.IsNullOrWhiteSpace(aliases)
            ? []
            : aliases.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static ModelPreset? Resolve(IEnumerable<ModelPreset> presets, string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return null;

        var list = presets as IReadOnlyList<ModelPreset> ?? presets.ToList();

        var byName = list.FirstOrDefault(p => p.Name == modelName);
        if (byName is not null)
            return byName;

        var byAlias = list.FirstOrDefault(p => ParseAliases(p.Aliases)
            .Any(a => !a.Contains('*') && string.Equals(a, modelName, StringComparison.OrdinalIgnoreCase)));
        if (byAlias is not null)
            return byAlias;

        return list.FirstOrDefault(p => ParseAliases(p.Aliases)
            .Any(a => a.Contains('*') && WildcardMatches(a, modelName)));
    }

    private static bool WildcardMatches(string pattern, string value)
    {
        var regex = "^" + string.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
