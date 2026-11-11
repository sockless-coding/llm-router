using System.Text;
using System.Text.RegularExpressions;

namespace LR.Core.Services;

/// <summary>
/// Picks out what explains a failed start from a server's output. The last lines alone rarely do: a
/// Python server prints its root cause first and then a chain of tracebacks that only say it failed.
/// </summary>
public static partial class StartupFailureSummary
{
    /// <summary>What the server log keeps of one entry; the full output is in <see cref="ServerOutputLog"/>.</summary>
    public const int MaxLength = 3800;

    private const int MaxErrorLines = 12;
    private const int TailLines = 6;
    private const int MaxLineLength = 400;

    /// <summary>
    /// A native crash (glibc's heap checks, a signal), since nothing after it explains more; else the first
    /// exception the output reports, which for a chained Python traceback is the root cause, unless all it
    /// says is to look further up; else the first line that says something went wrong. Null if nothing
    /// in the output looks like an error.
    /// </summary>
    public static string? LikelyCause(IReadOnlyList<string> lines)
    {
        string? firstException = null, firstError = null;
        foreach (var line in lines)
        {
            if (NativeCrashRegex().IsMatch(line))
                return Clip(line.Trim());
            if (firstException is null && ExceptionRegex().IsMatch(line) && !PointsElsewhereRegex().IsMatch(line))
                firstException = line;
            if (firstError is null && IsErrorLine(line))
                firstError = line;
        }
        var cause = firstException ?? firstError;
        return cause is null ? null : Clip(cause.Trim());
    }

    private static bool IsErrorLine(string line) =>
        ExceptionRegex().IsMatch(line) || NativeCrashRegex().IsMatch(line) || (ErrorRegex().IsMatch(line) && !TracebackRegex().IsMatch(line));

    /// <summary>
    /// <paramref name="headline"/>, the likely cause, the lines that report errors and the last lines of
    /// the output, without repeats and within <see cref="MaxLength"/>.
    /// </summary>
    public static string Describe(string headline, IReadOnlyList<string> lines, string? fullOutputPath = null)
    {
        var text = new StringBuilder(headline);
        var cause = LikelyCause(lines);
        if (cause is not null)
            text.Append(" Likely cause: ").Append(cause);

        var shown = new HashSet<string>(StringComparer.Ordinal);
        if (cause is not null) shown.Add(cause);

        var errors = lines
            .Where(IsErrorLine)
            .Select(l => Clip(l.Trim()))
            .Where(shown.Add)
            .Take(MaxErrorLines)
            .ToList();
        if (errors.Count > 0)
            text.Append("\n\nErrors reported:\n").AppendJoin('\n', errors);

        var tail = lines.TakeLast(TailLines).Select(l => Clip(l.TrimEnd())).Where(l => l.Length > 0 && !shown.Contains(l.Trim())).ToList();
        if (tail.Count > 0)
            text.Append("\n\nLast output:\n").AppendJoin('\n', tail);

        var footer = fullOutputPath is null ? "" : $"\n\nFull output: {fullOutputPath}";
        if (text.Length + footer.Length > MaxLength)
            text.Length = Math.Max(headline.Length, MaxLength - footer.Length - 1);
        return text.Append(footer).ToString();
    }

    private static string Clip(string line) => line.Length <= MaxLineLength ? line : line[..MaxLineLength] + "…";

    /// <summary>
    /// "RuntimeError: …", "torch.OutOfMemoryError: …", "ValueError: …", possibly after a log prefix.
    /// </summary>
    [GeneratedRegex(@"(?:^|[\s\]|)])(?:[A-Za-z_][\w]*\.)*[A-Za-z_]\w*(?:Error|Exception|Exit|Interrupt):\s", RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionRegex();

    [GeneratedRegex(@"\b(?:ERROR|CRITICAL|FATAL|Fatal|fatal error|error:|Error response from daemon)", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorRegex();

    /// <summary>A process killed by a signal or by glibc's heap checks ("malloc(): unaligned tcache chunk detected").</summary>
    [GeneratedRegex(@"\b(?:malloc|free|realloc|calloc|malloc_consolidate)\(\): |double free or corruption|corrupted (?:size|double-linked)|terminate called|Segmentation fault|core dumped|Fatal Python error|\bAborted\b|SIGSEGV|SIGABRT|SIGBUS|\bKilled\b", RegexOptions.CultureInvariant)]
    private static partial Regex NativeCrashRegex();

    /// <summary>vLLM's front end failing because its engine did, which only says to look further up.</summary>
    [GeneratedRegex(@"See root cause above|Engine core initialization failed", RegexOptions.CultureInvariant)]
    private static partial Regex PointsElsewhereRegex();

    /// <summary>
    /// A traceback's framing and frames, which say where rather than what: after an optional vLLM-style
    /// prefix like <c>(EngineCore pid=7) ERROR 11-09 20:31:22 [core.py:708] </c>, an indented line or one
    /// starting with <c>File "</c>, a caret or the chaining text.
    /// </summary>
    [GeneratedRegex(@"Traceback \(most recent call last\)|^(?:\([\w-]+ pid=\d+\) )?(?:[A-Z]+ [\d-]+ [\d:.]+ \[[^\]]*\] )?(?:\s+|File ""|\^|~|The above exception|During handling)", RegexOptions.CultureInvariant)]
    private static partial Regex TracebackRegex();
}
