using System.Text;
using System.Text.RegularExpressions;

using LR.Core.Models;

namespace LR.Core.Services.EngineBuilds;

/// <summary>
/// Surfaces llama.cpp's own build documentation and example scripts next to the recipe editor, and
/// does a best-effort parse of a <c>cmake</c> configure line into the structured fields a
/// <see cref="LlamaCppBuildRecipe"/> uses. It never edits a recipe on its own — the user reviews
/// what it extracts before applying it.
/// </summary>
public static class UpstreamBuildDocs
{
    /// <summary>Branch the reference docs are read from (docs at old tags are often missing/stale).</summary>
    public const string DocsRef = "master";

    /// <summary>Repo-relative doc / example-script paths to show for each backend.</summary>
    public static IReadOnlyList<string> SourcesFor(BackendType backend) => backend switch
    {
        BackendType.Cpu => new[] { "docs/build.md" },
        BackendType.Cuda => new[] { "docs/build.md" }, // CUDA has no separate backend doc
        BackendType.Vulkan => new[] { "docs/build.md" },
        BackendType.Sycl => new[]
        {
            "docs/backend/SYCL.md",
            "examples/sycl/win-build-sycl.bat",
            "examples/sycl/build.sh",
        },
        BackendType.Hip => new[] { "docs/build.md" },   // HIP has no separate backend doc
        BackendType.Metal => new[] { "docs/build.md" }, // Metal has no separate backend doc
        BackendType.Musa => new[] { "docs/build.md" },  // MUSA has no separate backend doc
        BackendType.OpenCL => new[] { "docs/build.md", "docs/backend/OPENCL.md" },
        BackendType.OpenVino => new[] { "docs/build.md", "docs/backend/OPENVINO.md" },
        BackendType.Cann => new[] { "docs/build.md", "docs/backend/CANN.md" },
        _ => new[] { "docs/build.md" },
    };

    public static string GitHubUrlFor(string repo, string path) =>
        $"https://github.com/{repo}/blob/{DocsRef}/{path}";

    // The accelerator flag each backend is identified by inside the shared build.md.
    private static readonly Dictionary<BackendType, string> BackendFlag = new()
    {
        [BackendType.Cuda] = "GGML_CUDA",
        [BackendType.Vulkan] = "GGML_VULKAN",
        [BackendType.Sycl] = "GGML_SYCL",
        [BackendType.Hip] = "GGML_HIP",
        [BackendType.Metal] = "GGML_METAL",
        [BackendType.OpenCL] = "GGML_OPENCL",
        [BackendType.OpenVino] = "GGML_OPENVINO",
        [BackendType.Musa] = "GGML_MUSA",
        [BackendType.Cann] = "GGML_CANN",
    };

    private static readonly string[] AllAcceleratorFlags =
    {
        "GGML_CUDA", "GGML_VULKAN", "GGML_SYCL", "GGML_HIP", "GGML_MUSA", "GGML_CANN",
        "GGML_METAL", "GGML_OPENCL", "GGML_OPENVINO", "GGML_BLAS", "GGML_ZENDNN",
    };

    /// <summary>
    /// Pulls candidate <c>cmake</c> configure commands out of a doc or script, keeping only the ones
    /// relevant to <paramref name="backend"/>: fenced code blocks in markdown, or the raw body of a
    /// <c>.sh</c>/<c>.bat</c>. Line continuations (<c>\</c> / <c>^</c>) are joined.
    /// </summary>
    public static IReadOnlyList<CommandBlock> ExtractCommandBlocks(string fileName, string content, BackendType backend)
    {
        var normalizedContent = content.Replace("\r\n", "\n");
        var isMarkdown = fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        var text = isMarkdown ? string.Join("\n", ExtractFencedBlocks(normalizedContent)) : normalizedContent;

        var joined = JoinContinuations(text);
        var results = new List<CommandBlock>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        const int maxPerSource = 12;
        foreach (var rawLine in joined.Split('\n'))
        {
            // A docs one-liner can chain "cmake -B … && cmake --build …" — consider each segment.
            foreach (var segment in rawLine.Split("&&"))
            {
                var trimmed = segment.Trim();
                var idx = trimmed.IndexOf("cmake ", StringComparison.Ordinal);
                if (idx < 0) continue;
                var command = StripComment(trimmed[idx..]).Trim();

                if (command.Contains("--build")) continue;
                if (!command.Contains("-D") && !command.Contains("-G")) continue;
                if (!IsRelevant(command, backend)) continue;
                if (!seen.Add(command)) continue;

                results.Add(new CommandBlock(command, TryParseCMake(command)));
                if (results.Count >= maxPerSource) return results;
            }
        }

        return results;
    }

    /// <summary>
    /// Whether a documented command applies to the chosen backend: it must mention that backend's
    /// accelerator flag, or (for CPU) mention no accelerator flag at all.
    /// </summary>
    private static bool IsRelevant(string command, BackendType backend)
    {
        var hasNoAccelerator = !AllAcceleratorFlags.Any(f => command.Contains(f, StringComparison.OrdinalIgnoreCase));

        // Metal is on by default on macOS, so its documented command is a plain `cmake -B build` —
        // show both the generic commands and any explicit GGML_METAL ones.
        if (backend == BackendType.Metal)
            return hasNoAccelerator || command.Contains("GGML_METAL", StringComparison.OrdinalIgnoreCase);

        if (BackendFlag.TryGetValue(backend, out var flag))
            return command.Contains(flag, StringComparison.OrdinalIgnoreCase);

        // CPU: skip anything that turns on another backend or a BLAS vendor.
        return hasNoAccelerator;
    }

    /// <summary>
    /// Tokenises a <c>cmake</c> configure command and splits it into the recipe fields: the
    /// <c>-D…</c> flags, the <c>-G</c> generator, and <c>CMAKE_BUILD_TYPE</c>. Positional source/binary
    /// dirs and build-driver flags are dropped; anything unrecognised is reported in
    /// <see cref="ParsedCMake.Ignored"/>. Returns null when the command can't be represented as a
    /// plain configure (e.g. it uses <c>--preset</c>) or carries no usable flags.
    /// </summary>
    public static ParsedCMake? TryParseCMake(string command)
    {
        var tokens = Tokenize(command);
        if (tokens.Count == 0) return null;

        var args = new List<string>();
        var ignored = new List<string>();
        string? generator = null;
        string? buildType = null;
        bool sawCmake = false;

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];

            if (!sawCmake)
            {
                if (t == "cmake") { sawCmake = true; continue; }
                if (Regex.IsMatch(t, @"^[A-Za-z_][A-Za-z0-9_]*=")) ignored.Add(t); // env prefix
                continue;
            }

            if (t == "--preset") return null; // CMakePresets.json flow — not our model

            switch (t)
            {
                case "-S" or "-B" or "-H":
                    i++; // skip its value
                    break;
                case "-G":
                    generator = i + 1 < tokens.Count ? Unquote(tokens[++i]) : null;
                    break;
                case "-D":
                    // "-D VAR=VAL" (space-separated form)
                    if (i + 1 < tokens.Count) AddDefine("-D" + tokens[++i], args, ref buildType);
                    break;
                case "-T" or "-A":
                    if (i + 1 < tokens.Count) ignored.Add($"{t} {tokens[++i]}");
                    break;
                case "-j" or "--parallel":
                    if (i + 1 < tokens.Count && int.TryParse(tokens[i + 1], out _)) i++;
                    break;
                case "." or "..":
                    break;
                default:
                    if (t.StartsWith("-G")) generator = Unquote(t[2..]);
                    else if (t.StartsWith("-D")) AddDefine(t, args, ref buildType);
                    else if (t.StartsWith("-")) ignored.Add(t);
                    break;
            }
        }

        if (args.Count == 0) return null;
        return new ParsedCMake(args, generator, buildType, ignored);
    }

    private static void AddDefine(string token, List<string> args, ref string? buildType)
    {
        if (token.StartsWith("-DCMAKE_BUILD_TYPE=", StringComparison.OrdinalIgnoreCase))
            buildType = token["-DCMAKE_BUILD_TYPE=".Length..].Trim();
        else
            args.Add(token);
    }

    private static IEnumerable<string> ExtractFencedBlocks(string markdown)
    {
        // Tolerate any fence info string (language + trailing spaces), require the newline after it.
        foreach (Match match in Regex.Matches(markdown, "```[^\\n]*\\n(.*?)```", RegexOptions.Singleline))
            yield return match.Groups[1].Value;
    }

    private static string JoinContinuations(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        normalized = Regex.Replace(normalized, @"\\\n\s*", " ");
        normalized = Regex.Replace(normalized, @"\^\n\s*", " ");
        return normalized;
    }

    /// <summary>Drops a trailing unquoted <c>#</c> shell comment.</summary>
    private static string StripComment(string command)
    {
        char quote = '\0';
        for (int i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '#' && i > 0 && char.IsWhiteSpace(command[i - 1])) return command[..i];
        }
        return command;
    }

    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';

        foreach (var c in command)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else sb.Append(c);
            }
            else if (c is '"' or '\'') quote = c;
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

    private static string Unquote(string s) => s.Trim('"', '\'');
}

/// <summary>One candidate build command lifted from an upstream doc/script, with its parse.</summary>
public record CommandBlock(string Command, ParsedCMake? Parsed);

/// <summary>The structured view of a <c>cmake</c> configure command.</summary>
public record ParsedCMake(List<string> CMakeArgs, string? Generator, string? BuildConfig, List<string> Ignored);
