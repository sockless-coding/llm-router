namespace LR.Core.Agents;

/// <summary>
/// Path handling for files on a remote agent's machine. The router may run on Windows while the
/// agent runs Linux (or the reverse), so <see cref="Path.Combine(string[])"/> — which uses the
/// router's own separator — can't be used to build paths that the agent will open.
/// </summary>
public static class AgentPaths
{
    public static char Separator(bool windows) => windows ? '\\' : '/';

    /// <summary>Joins path segments with the target OS's separator, normalising separators inside each segment.</summary>
    public static string Combine(bool windows, params string[] parts)
    {
        char sep = Separator(windows);
        var cleaned = new List<string>();
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            var s = windows ? part.Replace('/', '\\') : part.Replace('\\', '/');
            if (cleaned.Count == 0)
            {
                var trimmed = s.TrimEnd(sep);
                // Keep a bare root ("/" or "C:\") intact.
                cleaned.Add(trimmed.Length == 0 || (windows && trimmed.EndsWith(':')) ? trimmed + sep : trimmed);
            }
            else
            {
                s = s.Trim(sep);
                if (s.Length > 0) cleaned.Add(s);
            }
        }
        if (cleaned.Count == 0) return string.Empty;

        var result = cleaned[0];
        foreach (var segment in cleaned.Skip(1))
            result = result.EndsWith(sep) ? result + segment : result + sep + segment;
        return result;
    }

    /// <summary>The executable file name on the target OS (<c>llama-server</c> → <c>llama-server.exe</c> on Windows).</summary>
    public static string Executable(bool windows, string baseName)
    {
        var bare = baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? baseName[..^4] : baseName;
        return windows ? bare + ".exe" : bare;
    }

    /// <summary>The last segment of a target path, whichever separator it uses.</summary>
    public static string FileName(string path)
    {
        var i = path.LastIndexOfAny(['/', '\\']);
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>Turns a name into something safe to use as one folder or file name on any OS.</summary>
    public static string SafeSegment(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var chars = name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        return new string(chars).Trim().TrimEnd('.');
    }
}
